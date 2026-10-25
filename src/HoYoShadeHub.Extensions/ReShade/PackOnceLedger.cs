using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>Fail-closed 包账本。fence 仅诊断，绝不清账；旧 raw 哈希只允许精确迁移。</summary>
public static class PackOnceLedger
{
    public const string FileName = ".hysx_once.json";
    public enum Reservation { Reserved, Success, Pending, NeedsMigration }
    public enum PlanRegistration { Registered, Known, NeedsMigration }

    internal sealed class Ledger
    {
        public string Fence = string.Empty;
        // 原 flat/schema2 map 原样保留；未知 raw 哈希不可猜测完成状态。
        public Dictionary<string, bool> Entries = new(StringComparer.Ordinal);
        public Dictionary<string, string> States = new(StringComparer.Ordinal);
        public HashSet<string>? Plan;
    }

    private static string PathOf(string root) => Path.Combine(root, FileName);

    private static Ledger Read(string root)
    {
        string json;
        try { json = File.ReadAllText(PathOf(root)); }
        catch (FileNotFoundException) { return new Ledger(); }
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("once 账本不是 JSON object；拒绝执行。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (value.EnumerateObject().Any(item => !names.Add(item.Name)))
            throw new InvalidDataException("once 账本字段重复；拒绝执行。");
        var ledger = new Ledger();
        if (!value.TryGetProperty("schema", out JsonElement schema))
        {
            ReadEntries(value, ledger.Entries);
            return ledger;
        }
        if (!schema.TryGetInt32(out int version) || version is not (2 or 3)
            || !value.TryGetProperty("fence", out JsonElement fence) || fence.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("entries", out JsonElement entries))
            throw new InvalidDataException("once 账本 schema/fence/entries 损坏或不支持；拒绝执行。");
        ledger.Fence = fence.GetString()!;
        ReadEntries(entries, ledger.Entries);
        if (version == 3)
        {
            if (!value.TryGetProperty("states", out JsonElement states) || states.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("once 账本 states 损坏；拒绝执行。");
            foreach (JsonProperty item in states.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.String
                    || item.Value.GetString() is not ("pending" or "success")
                    || !ledger.States.TryAdd(item.Name, item.Value.GetString()!))
                    throw new InvalidDataException("once 账本状态未知或重复；拒绝执行。");
            }
            if (value.TryGetProperty("plan", out JsonElement plan))
            {
                if (plan.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("once 账本 plan 非法；拒绝执行。");
                ledger.Plan = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement key in plan.EnumerateArray())
                    if (key.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(key.GetString())
                        || !ledger.Plan.Add(key.GetString()!))
                        throw new InvalidDataException("once 账本 plan 含非法/重复身份；拒绝执行。");
            }
        }
        return ledger;
    }

    private static void ReadEntries(JsonElement entries, Dictionary<string, bool> target)
    {
        if (entries.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("once 旧账本不是 map；拒绝执行。");
        foreach (JsonProperty item in entries.EnumerateObject())
            if (item.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !target.TryAdd(item.Name, item.Value.GetBoolean()))
                throw new InvalidDataException("once 旧账本含非法/重复值；拒绝执行。");
    }

    private static void Write(string root, Ledger ledger)
    {
        var entries = new JsonObject();
        foreach (var item in ledger.Entries) entries[item.Key] = item.Value;
        var states = new JsonObject();
        foreach (var item in ledger.States) states[item.Key] = item.Value;
        var value = new JsonObject { ["schema"] = 3, ["fence"] = ledger.Fence,
            ["entries"] = entries, ["states"] = states };
        if (ledger.Plan is not null)
        {
            var plan = new JsonArray();
            foreach (string key in ledger.Plan.Order(StringComparer.Ordinal)) plan.Add(key);
            value["plan"] = plan;
        }
        string path = PathOf(root), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>整个包动作期间持有独占文件句柄：同进程/跨进程串行，崩溃由 OS 释放锁。</summary>
    public static async Task<Session> OpenAsync(string packRoot, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(packRoot) || !Directory.Exists(packRoot))
            throw new IOException("once 包根不存在；拒绝执行。");
        string root = Path.GetFullPath(packRoot);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(10);
        while (true)
        {
            FileStream file;
            try
            {
                file = new FileStream(PathOf(root) + ".lock", FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (timer.Elapsed < limit)
            {
                await Task.Delay(25).ConfigureAwait(false);
                continue;
            }
            try { return new Session(root, file, Read(root)); }
            catch { file.Dispose(); throw; }
        }
    }

    public sealed class Session : IDisposable
    {
        internal readonly string Root;
        private readonly FileStream _lock;
        internal readonly Ledger Data;
        private bool _disposed;
        internal Session(string root, FileStream file, Ledger ledger) { Root = root; _lock = file; Data = ledger; }

        public bool HasPlan => Data.Plan is not null;

        /// <summary>仅无历史的首次账本可注册整个已同意包。新 hash 同意不会扩大允许身份集合。</summary>
        public PlanRegistration RegisterPlan(IEnumerable<string> identities)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string[] keys = identities.ToArray();
            var plan = new HashSet<string>(keys, StringComparer.Ordinal);
            if (keys.Length == 0 || keys.Any(string.IsNullOrWhiteSpace) || plan.Count != keys.Length)
                throw new InvalidDataException("包 plan 为空或逻辑身份重复；需显式修正，拒绝执行。");
            if (Data.Plan is not null)
                return plan.IsSubsetOf(Data.Plan) ? PlanRegistration.Known : PlanRegistration.NeedsMigration;
            if (Data.Entries.Count > 0 || Data.States.Count > 0) return PlanRegistration.NeedsMigration;
            Data.Plan = plan;
            Write(Root, Data); // 整包身份必须先于任一步骤 reservation 落盘。
            return PlanRegistration.Registered;
        }

        public Reservation Reserve(string stableKey, string legacyKey)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Data.States.TryGetValue(stableKey, out string? state))
                return state == "success" ? Reservation.Success : Reservation.Pending;
            if ((Data.Entries.TryGetValue(stableKey, out bool stableDone) && stableDone)
                || (Data.Entries.TryGetValue(legacyKey, out bool done) && done))
            {
                Data.States[stableKey] = "success";
                Write(Root, Data);
                return Reservation.Success;
            }
            if (Data.Plan is null || !Data.Plan.Contains(stableKey)) return Reservation.NeedsMigration;
            Data.States[stableKey] = "pending";
            // 在任何副作用发生前 flush + atomic replace；失败直接抛出，不允许 dispatch。
            Write(Root, Data);
            return Reservation.Reserved;
        }

        public void Complete(string stableKey)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!Data.States.TryGetValue(stableKey, out string? state) || state != "pending")
                throw new InvalidOperationException("动作没有 pending reservation。");
            Data.States[stableKey] = "success";
            Write(Root, Data); // 失败时磁盘仍是 pending，下次不会自动重试。
        }

        /// <summary>测试和生产共用的副作用入口：先持久化 pending；异常/partial fail 永不自动重试。</summary>
        public async Task<(Reservation Reservation, string? Outcome)> ExecuteAsync(
            string stableKey, string legacyKey, Func<Task<string>> dispatch)
        {
            Reservation reservation = Reserve(stableKey, legacyKey);
            if (reservation != Reservation.Reserved) return (reservation, null);
            string outcome = await dispatch();
            if (PackActionOncePolicy.IsSuccessfulOutcome(outcome)) Complete(stableKey);
            return (reservation, outcome);
        }

        public void Dispose() { if (!_disposed) { _disposed = true; _lock.Dispose(); } }
    }

    public static string ReadFence(string packRoot) => Read(packRoot).Fence;

    /// <returns>诊断 fence 是否改变；不论是否改变，所有旧账和 pending 都保留。</returns>
    public static bool ApplyFence(string packRoot, string fence)
    {
        using Session session = OpenAsync(packRoot).GetAwaiter().GetResult();
        if (session.Data.Fence == fence) return false;
        session.Data.Fence = fence;
        Write(session.Root, session.Data);
        return true;
    }

    // 兼容既有调用；读取错误不吞，旧 map 的 true 仍有效。
    public static bool Has(string packRoot, string key)
    {
        Ledger ledger = Read(packRoot);
        return ledger.Entries.TryGetValue(key, out bool done) && done
            || ledger.States.TryGetValue(key, out string? state) && state is "success" or "pending";
    }

    public static void Mark(string packRoot, string key)
    {
        using Session session = OpenAsync(packRoot).GetAwaiter().GetResult();
        session.Data.Entries[key] = true;
        Write(session.Root, session.Data);
    }

    /// <summary>
    /// 重新导入同一个覆盖包时开始新的动作批次。保留锁文件用于并发协调，
    /// 删除的只是本包的完成/待处理记录；下一次执行会重新注册完整 plan。
    /// </summary>
    public static bool ResetForReimport(string packRoot)
    {
        if (string.IsNullOrWhiteSpace(packRoot) || !Directory.Exists(packRoot))
        {
            return false;
        }

        using Session session = OpenAsync(packRoot).GetAwaiter().GetResult();
        string path = PathOf(session.Root);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
