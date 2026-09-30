// DLSS5 Preset Switcher - a small ReShade add-on for live INI presets.
//
// Presets are ordinary UTF-8 INI files placed beside this add-on or under
// DLSS5-Presets\. The add-on switches ReShade effect presets when an INI has
// effect sections, and writes RenoDX/DLSS5 sections through ReShade's config
// API so renodx-dlss.addon64 and renodx-dlss5.addon64 can observe the change.
//
// The add-on deliberately does not edit ReShade.ini directly. ReShade's
// ReShadeSetConfigValue API performs the write using the host's active config
// and avoids races with ReShade's own configuration writer.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#define ImTextureID ImU64
#include <imgui.h>
#include <reshade.hpp>

#include <algorithm>
#include <cctype>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <map>
#include <set>
#include <sstream>
#include <string>
#include <utility>
#include <vector>

namespace fs = std::filesystem;

namespace {

constexpr const char *kName = "DLSS5 Preset Switcher";
constexpr const char *kVersion = "1.0.0";
constexpr const char *kOverlayTitle = "DLSS5 Presets";
constexpr const char *kConfigFile = "dlss5-preset-switcher.ini";
constexpr const char *kPresetFolder = "DLSS5-Presets";

fs::path g_addon_dir;
std::vector<fs::path> g_presets;
std::string g_status = "Ready";
std::string g_active_name;
fs::path g_active_path;
fs::file_time_type g_active_write_time{};
bool g_auto_reload = true;
bool g_registered = false;

struct IniEntry {
    std::string section;
    std::string key;
    std::string value;
};

struct IniFile {
    std::vector<IniEntry> entries;
    bool has_effect_sections = false;
    bool has_reshade_preset_keys = false;
};

struct BaselineValue {
    std::string section;
    std::string key;
    std::string value;
    bool existed = false;
};

std::map<std::string, BaselineValue> g_baseline;
std::string g_original_preset_path;
bool g_baseline_captured = false;

std::string Lower(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return value;
}

std::string Trim(std::string value)
{
    const auto first = value.find_first_not_of(" \t\r\n");
    if (first == std::string::npos)
        return {};
    const auto last = value.find_last_not_of(" \t\r\n");
    return value.substr(first, last - first + 1);
}

std::string Utf8(const fs::path &path)
{
    const std::wstring wide = path.wstring();
    if (wide.empty())
        return {};
    const int length = WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()),
        nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()), result.data(), length,
        nullptr, nullptr);
    return result;
}

void Log(const std::string &message, reshade::log::level level = reshade::log::level::info)
{
    reshade::log::message(level, message.c_str());
}

void SetStatus(const std::string &message, reshade::log::level level = reshade::log::level::info)
{
    g_status = message;
    Log(std::string("[DLSS5 Preset Switcher] ") + message, level);
}

bool ReadText(const fs::path &path, std::string &text)
{
    std::ifstream file(path, std::ios::binary);
    if (!file)
        return false;

    text.assign(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
    if (text.size() >= 3 && static_cast<unsigned char>(text[0]) == 0xEF &&
        static_cast<unsigned char>(text[1]) == 0xBB && static_cast<unsigned char>(text[2]) == 0xBF)
        text.erase(0, 3);

    // Windows INI files are often saved as UTF-16. The configuration API is
    // UTF-8, so reject that format with a useful status rather than parsing
    // embedded NUL characters as keys.
    if (text.size() >= 2 && ((static_cast<unsigned char>(text[0]) == 0xFF &&
        static_cast<unsigned char>(text[1]) == 0xFE) || (static_cast<unsigned char>(text[0]) == 0xFE &&
        static_cast<unsigned char>(text[1]) == 0xFF)))
        return false;
    return true;
}

bool ParseIni(const fs::path &path, IniFile &result)
{
    std::string text;
    if (!ReadText(path, text))
        return false;

    std::istringstream stream(text);
    std::string line;
    std::string section;
    while (std::getline(stream, line)) {
        line = Trim(line);
        if (line.empty() || line[0] == ';' || line[0] == '#')
            continue;
        if (line.front() == '[' && line.back() == ']') {
            section = Trim(line.substr(1, line.size() - 2));
            continue;
        }
        const auto equal = line.find('=');
        if (equal == std::string::npos || section.empty())
            continue;
        const std::string key = Trim(line.substr(0, equal));
        if (key.empty())
            continue;
        const std::string value = Trim(line.substr(equal + 1));
        result.entries.push_back({ section, key, value });

        const std::string lower_section = Lower(section);
        const std::string lower_key = Lower(key);
        if (lower_section == "general" && (lower_key == "techniques" ||
            lower_key == "techniquesorting" || lower_key == "preprocessordefinitions"))
            result.has_reshade_preset_keys = true;
        if (lower_section.size() >= 3 && lower_section.substr(lower_section.size() - 3) == ".fx")
            result.has_effect_sections = true;
    }
    return true;
}

bool IsReserved(const fs::path &path)
{
    const std::string name = Lower(path.filename().string());
    return name == "reshade.ini" || name == "reshade2.ini" || name == Lower(kConfigFile) ||
        name == "dlss5-preset-switcher.log";
}

void AddPreset(const fs::path &path, std::set<std::string> &seen)
{
    if (!fs::is_regular_file(path) || Lower(path.extension().string()) != ".ini" || IsReserved(path))
        return;
    std::error_code error;
    const fs::path canonical = fs::weakly_canonical(path, error);
    const std::string identity = Lower(Utf8(error ? path : canonical));
    if (seen.insert(identity).second)
        g_presets.push_back(path);
}

void ScanPresetDirectory(const fs::path &directory, bool recursive, std::set<std::string> &seen)
{
    std::error_code error;
    if (!fs::is_directory(directory, error))
        return;
    if (recursive) {
        for (const auto &entry : fs::recursive_directory_iterator(directory, error)) {
            if (error)
                break;
            AddPreset(entry.path(), seen);
        }
    } else {
        for (const auto &entry : fs::directory_iterator(directory, error)) {
            if (error)
                break;
            AddPreset(entry.path(), seen);
        }
    }
}

void RefreshPresets()
{
    g_presets.clear();
    std::set<std::string> seen;
    // Direct INIs preserve the requested "put it next to the plugin" workflow.
    ScanPresetDirectory(g_addon_dir, false, seen);
    // The subfolder is recommended and can be organized recursively.
    ScanPresetDirectory(g_addon_dir / kPresetFolder, true, seen);
    std::sort(g_presets.begin(), g_presets.end(), [](const fs::path &a, const fs::path &b) {
        return Lower(a.filename().string()) < Lower(b.filename().string());
    });
}

bool IsProtectedSection(const std::string &section)
{
    const std::string lower = Lower(section);
    // These are host-level settings, not per-preset visual/DLSS settings. A
    // shared preset must not silently disable add-ons or change input keys.
    static const char *const protected_sections[] = {
        "addon", "general", "input", "overlay", "screenshot", "style", "depth"
    };
    for (const char *protected_section : protected_sections)
        if (lower == protected_section)
            return true;
    return false;
}

bool IsGlobalPresetSection(const std::string &section)
{
    // RenoDX names its sections RENODX-DLSS and RENODX-DLSS5. DLSS5 feed and
    // future RenoDX sections are intentionally accepted by prefix as well.
    const std::string lower = Lower(section);
    return lower.rfind("renodx-", 0) == 0 || lower.rfind("renodx_", 0) == 0 ||
        lower.rfind("dlss5", 0) == 0 || (!IsProtectedSection(section) &&
        lower.find("renodx") != std::string::npos);
}

bool GetGlobalValue(const std::string &section, const std::string &key, std::string &value)
{
    char buffer[4096] = {};
    size_t size = sizeof(buffer);
    if (!reshade::get_config_value(nullptr, section.c_str(), key.c_str(), buffer, &size))
        return false;
    value.assign(buffer);
    return true;
}

std::string ConfigIdentity(const std::string &section, const std::string &key)
{
    return Lower(section) + "\n" + Lower(key);
}

void SnapshotBaseline(const std::string &section, const std::string &key)
{
    const std::string identity = ConfigIdentity(section, key);
    if (g_baseline.find(identity) != g_baseline.end())
        return;
    BaselineValue baseline{ section, key, {}, false };
    baseline.existed = GetGlobalValue(section, key, baseline.value);
    g_baseline.emplace(identity, std::move(baseline));
}

void RestoreBaseline()
{
    for (const auto &[identity, baseline] : g_baseline) {
        // ReShade's public config API has no delete operation. Empty is the
        // least surprising representation for a key that was absent.
        reshade::set_config_value(nullptr, baseline.section.c_str(), baseline.key.c_str(),
            baseline.existed ? baseline.value.c_str() : "");
    }
}

std::string CurrentPresetPath(reshade::api::effect_runtime *runtime)
{
    if (runtime == nullptr)
        return {};
    char path[4096] = {};
    runtime->get_current_preset_path(path);
    return path;
}

bool ApplyPreset(reshade::api::effect_runtime *runtime, const fs::path &path, bool automatic)
{
    IniFile ini;
    if (!ParseIni(path, ini)) {
        SetStatus("Cannot read preset (use UTF-8 INI): " + Utf8(path.filename()), reshade::log::level::warning);
        return false;
    }

    if (!g_baseline_captured) {
        if (runtime != nullptr)
            g_original_preset_path = CurrentPresetPath(runtime);
        g_baseline_captured = true;
    }

    RestoreBaseline();
    for (const IniEntry &entry : ini.entries) {
        if (!IsGlobalPresetSection(entry.section))
            continue;
        SnapshotBaseline(entry.section, entry.key);
        reshade::set_config_value(nullptr, entry.section.c_str(), entry.key.c_str(), entry.value.c_str());
    }

    if (runtime != nullptr && (ini.has_effect_sections || ini.has_reshade_preset_keys)) {
        runtime->set_current_preset_path(Utf8(path).c_str());
    }

    g_active_path = path;
    g_active_name = Utf8(path.filename());
    std::error_code error;
    g_active_write_time = fs::last_write_time(path, error);
    SetStatus(std::string(automatic ? "Auto-reloaded: " : "Applied: ") + g_active_name);
    return true;
}

void ClearPreset(reshade::api::effect_runtime *runtime)
{
    RestoreBaseline();
    if (runtime != nullptr && !g_original_preset_path.empty())
        runtime->set_current_preset_path(g_original_preset_path.c_str());
    g_active_name.clear();
    g_active_path.clear();
    g_original_preset_path.clear();
    g_baseline.clear();
    g_baseline_captured = false;
    SetStatus("Cleared; original ReShade/RenoDX settings restored");
}

void AutoReloadIfChanged(reshade::api::effect_runtime *runtime)
{
    if (!g_auto_reload || g_active_path.empty() || !fs::exists(g_active_path))
        return;
    std::error_code error;
    const auto write_time = fs::last_write_time(g_active_path, error);
    if (!error && write_time != g_active_write_time)
        ApplyPreset(runtime, g_active_path, true);
}

void DrawOverlay(reshade::api::effect_runtime *runtime)
{
    RefreshPresets();
    AutoReloadIfChanged(runtime);

    ImGui::Text("Drop UTF-8 .ini files beside the add-on or in DLSS5-Presets\\");
    ImGui::Text("RenoDX sections are applied through ReShade config API.");
    ImGui::Checkbox("Auto-reload active file", &g_auto_reload);
    ImGui::SameLine();
    if (ImGui::Button("Refresh files"))
        RefreshPresets();
    ImGui::Separator();

    if (g_presets.empty()) {
        ImGui::TextDisabled("No .ini presets found.");
    } else {
        for (size_t i = 0; i < g_presets.size(); ++i) {
            const fs::path &path = g_presets[i];
            const bool active = !g_active_path.empty() && Lower(Utf8(path)) == Lower(Utf8(g_active_path));
            const std::string label = Utf8(path.lexically_relative(g_addon_dir));
            ImGui::PushID(static_cast<int>(i));
            if (ImGui::Selectable(label.c_str(), active))
                ApplyPreset(runtime, path, false);
            ImGui::SameLine();
            if (ImGui::SmallButton("Apply"))
                ApplyPreset(runtime, path, false);
            ImGui::PopID();
        }
    }

    ImGui::Separator();
    if (ImGui::Button("Clear / restore original"))
        ClearPreset(runtime);
    ImGui::SameLine();
    ImGui::Text("%s", g_status.c_str());
    if (!g_active_name.empty())
        ImGui::Text("Active: %s", g_active_name.c_str());
}

} // namespace

extern "C" __declspec(dllexport) const char *NAME = kName;
extern "C" __declspec(dllexport) const char *DESCRIPTION =
    "Live INI preset switcher for RenoDX DLSS and DLSS5 ReShade add-ons";

BOOL WINAPI DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        wchar_t module_path[MAX_PATH] = {};
        GetModuleFileNameW(module, module_path, MAX_PATH);
        g_addon_dir = fs::path(module_path).parent_path();
        RefreshPresets();

        if (!reshade::register_addon(module))
            return FALSE;
        g_registered = true;
        reshade::register_overlay(kOverlayTitle, DrawOverlay);
        Log(std::string(kName) + " " + kVersion + " loaded; " + std::to_string(g_presets.size()) + " preset(s)");
    } else if (reason == DLL_PROCESS_DETACH && reserved == nullptr) {
        if (g_registered) {
            reshade::unregister_overlay(kOverlayTitle, DrawOverlay);
            reshade::unregister_addon(module);
            g_registered = false;
        }
    }
    return TRUE;
}
