#define _CRT_SECURE_NO_WARNINGS
#define NOMINMAX

#include <iostream>
#include <filesystem>
#include <format>
#include <Windows.h>
#include "INIReader.h"

#pragma comment(linker, "/subsystem:windows /entry:wmainCRTStartup")

using namespace std::filesystem;


HANDLE stdOut;


void Log(std::wstring output)
{
	if (stdOut)
	{
		SYSTEMTIME time;
		GetSystemTime(&time);
		std::wstring timeStr = std::format(L"[{:02}:{:02}:{:02}.{:03}] ", time.wHour, time.wMinute, time.wSecond, time.wMilliseconds);
        WriteConsole(stdOut, timeStr.c_str(), static_cast<DWORD>(timeStr.length()), NULL, NULL);
        WriteConsole(stdOut, output.c_str(), static_cast<DWORD>(output.length()), NULL, NULL);
		WriteConsole(stdOut, L"\r\n", 2, NULL, NULL);
	}
}

std::wstring QuoteArgument(const std::wstring& value)
{
    if (!value.empty() && value.find_first_of(L" \t\"") == std::wstring::npos)
        return value;
    std::wstring result = L"\"";
    size_t slashes = 0;
    for (const wchar_t c : value)
    {
        if (c == L'\\') { ++slashes; continue; }
        result.append(c == L'"' ? slashes * 2 + 1 : slashes, L'\\');
        result += c;
        slashes = 0;
    }
    result.append(slashes * 2, L'\\');
    return result + L'"';
}

bool IsCliVerb(const wchar_t* value)
{
    for (const auto verb : { L"run", L"auto", L"action", L"stopgame", L"killgame", L"startgame",
        L"testgame", L"test-game", L"selftest", L"collectlogs", L"collect-logs", L"sessionlogs" })
        if (!_wcsicmp(value, verb)) return true;
    return false;
}


int wmain(int argc, wchar_t* argv[])
{
    const bool cli_mode = argc > 1 && IsCliVerb(argv[1]);
    if (cli_mode && GetFileType(GetStdHandle(STD_OUTPUT_HANDLE)) != FILE_TYPE_PIPE
        && !AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();
	for (size_t i = 0; i < argc; i++)
	{
		if (!wcscmp(argv[i], L"--trace"))
		{
			AllocConsole();
		}
	}

	stdOut = GetStdHandle(STD_OUTPUT_HANDLE);

	std::wstring run_exe;

    wchar_t module_path[32768];
    const DWORD module_length = GetModuleFileNameW(NULL, module_path, 32768);
    if (!module_length || module_length >= 32768) return 1;
    auto base_folder = path(module_path).parent_path();
	auto version = path(base_folder).append("version.ini");
	if (exists(version))
	{
		INIReader ini(version.string());
		if (ini.ParseError() == 0)
		{
			auto exe_path = ini.Get("", "exe_path", "");
			if (exe_path.length())
			{
				auto target_exe = path(base_folder).append(exe_path);
                const auto relative = path(exe_path);
                if (!relative.is_absolute() && relative.parent_path().parent_path().empty()
                    && relative.parent_path().wstring().starts_with(L"app-")
                    && relative.filename() == L"HoYoShadeHub.exe" && exists(target_exe))
				{
					run_exe = target_exe.wstring();
				}
			}
		}
	}
	else
	{
		Log(L"version.ini not found");
	}

	if (!run_exe.length())
	{
		path target_exe;
        file_time_type last_time = file_time_type::min();
		for (auto folder : directory_iterator(base_folder))
		{
            if (folder.is_directory() && folder.path().filename().wstring().starts_with(L"app-"))
			{
				auto exe = path(folder).append(L"HoYoShadeHub.exe");
				if (exists(exe))
				{
					auto time = last_write_time(exe);
					if (time > last_time)
					{
						target_exe = exe;
						last_time = time;
					}
				}
			}
		}
		run_exe = target_exe.wstring();
	}

	Log(L"run_exe: " + run_exe);

	if (run_exe.length())
	{
        std::wstring arg = QuoteArgument(run_exe);
        for (int i = 1; i < argc; ++i) arg += L" " + QuoteArgument(argv[i]);
		Log(L"arg: " + arg);
		STARTUPINFO si;
		PROCESS_INFORMATION pi;
		ZeroMemory(&si, sizeof(si));
		si.cb = sizeof(si);
        if (cli_mode)
        {
            si.dwFlags |= STARTF_USESTDHANDLES;
            si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
            si.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
            si.hStdError = GetStdHandle(STD_ERROR_HANDLE);
            for (const auto handle : { si.hStdInput, si.hStdOutput, si.hStdError })
                if (handle && handle != INVALID_HANDLE_VALUE)
                    SetHandleInformation(handle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);
        }
		ZeroMemory(&pi, sizeof(pi));
		Log(L"Starting process");
        if (!CreateProcessW(run_exe.c_str(), arg.data(), NULL, NULL, cli_mode, 0, NULL,
            base_folder.c_str(), &si, &pi))
        {
            Log(std::format(L"Process creation failed ({})", GetLastError()));
            return 1;
        }
		Log(std::format(L"Process started ({})", GetProcessId(pi.hProcess)));
        DWORD exit_code = 0;
        if (cli_mode)
        {
            WaitForSingleObject(pi.hProcess, INFINITE);
            if (!GetExitCodeProcess(pi.hProcess, &exit_code)) exit_code = 1;
        }
		CloseHandle(pi.hProcess);
		CloseHandle(pi.hThread);

        // Keep previous releases available for rollback after a failed update.
        if (cli_mode) return static_cast<int>(exit_code);
	}
	else
	{
		Log(L"HoYoShadeLauncher.exe not found");
		SetProcessDPIAware();
		auto ok = MessageBox(NULL, L"HoYoShade Hub files not found.\r\nWould you like to download it now?\r\nhttps://github.com/DuolaD/HoYoShade-Hub", L"HoYoShade Hub", MB_ICONWARNING | MB_OKCANCEL);
		if (ok == IDOK)
		{
			ShellExecute(NULL, NULL, L"https://github.com/DuolaD/HoYoShade-Hub", NULL, NULL, SW_SHOWNORMAL);
		}
	}

	if (stdOut)
	{
		Log(L"Wait for 10s to exit...");
		Sleep(10000);
	}
}

