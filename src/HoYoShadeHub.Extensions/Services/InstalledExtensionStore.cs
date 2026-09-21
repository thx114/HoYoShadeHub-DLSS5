using HoYoShadeHub.Extensions.Models;
using System.Text.Json;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>
/// 读写 <c>&lt;HoYoShade&gt;\.hysx\installed.json</c>。
/// 账本是「这个目录里哪些文件是我们装的」的唯一事实来源，卸载完全依赖它。
/// </summary>
public sealed class InstalledExtensionStore
{
    private readonly ShadeHost _host;

    public InstalledExtensionStore(ShadeHost host)
    {
        _host = host;
    }

    public async Task<InstalledExtensionLedger> LoadAsync(CancellationToken cancellationToken = default)
    {
        string path = _host.LedgerPath;
        if (!File.Exists(path))
        {
            return new InstalledExtensionLedger();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var ledger = await JsonSerializer.DeserializeAsync<InstalledExtensionLedger>(
                stream, InstalledExtensionLedger.JsonOptions, cancellationToken);
            return ledger ?? new InstalledExtensionLedger();
        }
        catch (JsonException)
        {
            // 账本损坏：改名留档，避免把锅甩给用户
            TryBackupCorruptedLedger(path);
            return new InstalledExtensionLedger();
        }
    }

    public async Task SaveAsync(InstalledExtensionLedger ledger, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_host.MetadataPath);

        string path = _host.LedgerPath;
        string temp = path + ".tmp";

        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, ledger, InstalledExtensionLedger.JsonOptions, cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
    }

    public async Task<InstalledExtension?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        var ledger = await LoadAsync(cancellationToken);
        return ledger.Extensions.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task UpsertAsync(InstalledExtension record, CancellationToken cancellationToken = default)
    {
        var ledger = await LoadAsync(cancellationToken);
        ledger.Extensions.RemoveAll(e => string.Equals(e.Id, record.Id, StringComparison.OrdinalIgnoreCase));
        ledger.Extensions.Add(record);
        await SaveAsync(ledger, cancellationToken);
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        var ledger = await LoadAsync(cancellationToken);
        int removed = ledger.Extensions.RemoveAll(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            await SaveAsync(ledger, cancellationToken);
        }
        return removed > 0;
    }

    private static void TryBackupCorruptedLedger(string path)
    {
        try
        {
            string backup = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(path, backup, overwrite: true);
        }
        catch
        {
            // ignore
        }
    }
}
