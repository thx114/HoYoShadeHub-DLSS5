namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>Only an explicit installation opens a new pack action generation.</summary>
public static class PackInstallTransaction
{
    public const string FileName = ".hysx_install_pending";
    private const string PreparedFileName = ".hysx_install_prepared";

    public static void Start(string packRoot)
    {
        Directory.CreateDirectory(packRoot);
        // Payloads may contain stale local markers; always replace their generation.
        File.Delete(Path.Combine(packRoot, PreparedFileName));
        File.WriteAllText(Path.Combine(packRoot, FileName), Guid.NewGuid().ToString("N"));
    }

    public static bool IsPending(string packRoot) => File.Exists(Path.Combine(packRoot, FileName));

    public static async Task<bool> PrepareAsync(string packRoot, TimeSpan? timeout = null)
    {
        if (!IsPending(packRoot)) return false;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(10);
        FileStream gate;
        while (true)
        {
            try
            {
                gate = new FileStream(Path.Combine(packRoot, PackOnceLedger.FileName + ".lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException) when (timer.Elapsed < limit)
            {
                await Task.Delay(25).ConfigureAwait(false);
            }
        }
        using (gate)
        {
            if (!IsPending(packRoot)) return false;
            string generation = File.ReadAllText(Path.Combine(packRoot, FileName)).Trim();
            if (!Guid.TryParseExact(generation, "N", out _))
                throw new InvalidDataException("Invalid pack installation generation.");
            string prepared = Path.Combine(packRoot, PreparedFileName);
            if (File.Exists(prepared) && File.ReadAllText(prepared).Trim() == generation) return true;
            string history = Path.Combine(packRoot, ".install-history", generation);
            Directory.CreateDirectory(history);
            foreach (string name in new[] { PackOnceLedger.FileName, ".hysx_ini_config_applied" })
            {
                string source = Path.Combine(packRoot, name);
                string archived = Path.Combine(history, name);
                // A crash between archive and deletion must never replace the original archive.
                if (File.Exists(source))
                {
                    if (!File.Exists(archived)) File.Copy(source, archived);
                    File.Delete(source);
                }
            }
            File.WriteAllText(prepared, generation);
            return true;
        }
    }

    public static void Complete(string packRoot) => File.Delete(Path.Combine(packRoot, FileName));
}
