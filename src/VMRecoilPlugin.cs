using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace VMRecoil
{
    /// <summary>
    /// CS2 viewmodel_recoil 开关（v2.4.0）。
    ///
    /// 能力：
    ///   1) 把 client.dll 中控制 viewmodel 后坐力缩放的常量（0.325f）改成 0.0f / 恢复，实现开关；
    ///   2) 注册真实 float cvar viewmodel_recoil（默认 1，范围 [0,1]），走引擎原生补全，不触发 Panorama 闪退；
    ///   3) 用户改动 cvar 后自动落盘（插件目录 vmrecoil_user.cfg），重开游戏 / 换图 / 重载插件自动读回并写回 cvar。
    ///
    /// v2.4.0 相对 v2.3.0 的修改（对应审查 12 条）：
    ///   - 删除 Probe 的"自检写入 0 再写回 1"，探针改为纯只读（原 P0：会覆盖用户 OFF 设置）；
    ///   - 新增 Unload：卸载/重载时把常量恢复成 0.325f（原 P0：OFF 状态下卸载会让后坐力永久失效）；
    ///   - Apply 写完回读校验，失败重试一次，仍不符则不改 state（原 P1：盲更新 _state）；
    ///   - Locate 三重校验：RVA 直读必须为 0.325f，否则必须由签名扫描反算出同一地址才认（0.0f 不再单独采信）；
    ///   - VirtualProtect 改为整页保护 + finally 还原，避免 4 字节跨页（原 P1）；
    ///   - Load 全包 try/catch；Poll 读不到值不再静默（连续 NaN 计数告警 + 句柄刷新）；
    ///   - 轮询改为"以 cvar 为唯一真值"：每次比对常量与期望值，漂移自愈；心跳与变化日志降噪。
    ///
    /// 说明：cvar 注册用的 3 块非托管内存 + 字符串指针被引擎 cvar 表长期引用，
    ///       故意不做 FreeHGlobal（释放会造成 cvar 表悬挂指针 → 崩溃），进程级常驻，体量几十字节。
    /// </summary>
    public class VMRecoilPlugin : BasePlugin
    {
        public override string ModuleName => "VMRecoil";
        public override string ModuleVersion => "2.4.0";
        public override string ModuleAuthor => "tonysinke";
        public override string ModuleDescription => "viewmodel_recoil as a real cvar (1=on 0=off, value persisted)";

        private const string CvarName = "viewmodel_recoil";

        // 定位基准：client.dll 1.40.4.5（SHA1 A7A6BD4A53A31DFFBA2F89A5AAD57D0C278D6638）中 .rdata 的 0.325f 常量
        private const int ConstRva = 0x12E11BC;      // RVA of 0.325f in .rdata
        private const float RecoilScale = 0.325f;

        private const uint PageExecuteReadWrite = 0x40;
        private const string StateFileName = "vmrecoil_user.cfg";

        // cvar 注册链
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ConVarRefInitFn(IntPtr refPtr, int unk1, short idx);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RegCoreFn(IntPtr refPtr, IntPtr name, ulong flags,
                                        IntPtr help, IntPtr desc, IntPtr unused);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate float GetFloatFn(IntPtr refPtr);

        private IntPtr _addr = IntPtr.Zero;
        private int _state = 1;             // 1 = 原版（0.325f），0 = 关闭（0.0f）
        private bool _unloaded;

        private IntPtr _cvarRef = IntPtr.Zero;
        private IntPtr _cvarNamePtr = IntPtr.Zero;
        private IntPtr _cvarHelpPtr = IntPtr.Zero;
        private IntPtr _cvarDescPtr = IntPtr.Zero;
        private GetFloatFn _getFloat;
        private ConVar _existingCvar;

        private string _lastCssError = "";
        private float _lastSeenValue = float.NaN;   // 上一次读到的 cvar 值
        private float _lastSavedValue = float.NaN;  // 上一次落盘的值
        private float _pendingRestore = float.NaN;  // 待写回 cvar 的用户设置（NaN = 无）
        private int _restoreTicks;
        private int _restoreAttempts;
        private int _nanStreak;
        private int _pollCount;

        // ------------------------------------------------------------ Load / Unload
        public override void Load(bool hotReload)
        {
            try
            {
                LoadCore(hotReload);
            }
            catch (Exception ex)
            {
                Logger.LogError("[VMRecoil] Load 异常，插件降级为空转（不改动任何内存）：{0}: {1}", ex.GetType().Name, ex.Message);
            }
        }

        private void LoadCore(bool hotReload)
        {
            _unloaded = false;

            _addr = Locate();
            if (_addr == IntPtr.Zero)
            {
                Logger.LogError("[VMRecoil] 未能定位 viewmodel recoil 常量（client.dll 可能已更新），本次不会改动任何内存；" +
                                "用户设置仍会被读取，但无法生效。");
            }
            else
            {
                float cur = ReadFloat(_addr);
                _state = Math.Abs(cur) < 1e-6f ? 0 : 1;
                Logger.LogInformation("[VMRecoil] 常量已定位 addr={0:X} 当前值={1:F6} state={2}",
                    _addr.ToInt64(), cur, _state);
            }

            EnsureCvar();
            RefreshCssHandle();

            // 读回上次保存的用户设置
            float saved = LoadSavedValue();
            _lastSavedValue = saved;

            float cv = ReadCvarValue(out string ch0);
            Logger.LogInformation("[VMRecoil] 起始读值 {0}（通道={1}）", Fmt(cv), ch0);

            float target;
            if (!float.IsNaN(saved))
            {
                target = saved;
                if (!float.IsNaN(cv) && Math.Abs(cv - saved) < 1e-3f)
                {
                    Logger.LogInformation("[VMRecoil] cvar 当前值已是用户设置 {0}，无需写回", Fmt(saved));
                }
                else if (!float.IsNaN(cv))
                {
                    Logger.LogInformation("[VMRecoil] 恢复用户设置：{0}（当前 cvar={1}）", Fmt(saved), Fmt(cv));
                    BeginRestore(saved);
                }
                else
                {
                    Logger.LogWarning("[VMRecoil] cvar 读不到值，暂无法写回用户设置 {0}；轮询期间会继续重试", Fmt(saved));
                    BeginRestore(saved);
                }
            }
            else
            {
                target = float.IsNaN(cv) ? 1f : cv;
                Logger.LogInformation("[VMRecoil] 首次运行（无用户设置文件），按当前值 {0} 启动", Fmt(target));
            }

            if (_addr != IntPtr.Zero)
            {
                int want = target >= 0.5f ? 1 : 0;
                if (Math.Abs(ReadFloat(_addr) - Expected(want)) > 1e-5f) Apply(want, "Load");
                else _state = want;
            }

            AddTimer(1.0f, Probe);                          // 只读可见性检查
            AddTimer(0.1f, Poll, TimerFlags.REPEAT);        // 轮询 cvar
            AddTimer(30.0f, Heartbeat, TimerFlags.REPEAT);  // 低频心跳

            Logger.LogInformation("[VMRecoil] v{0} 就绪：{1} 为真实 cvar（cvarRef={2}），state={3}，设置文件={4}",
                ModuleVersion, CvarName, _cvarRef == IntPtr.Zero ? "find" : "self", _state, StatePath());
        }

        public override void Unload(bool hotReload)
        {
            _unloaded = true;
            try
            {
                if (_addr != IntPtr.Zero)
                {
                    float cur = ReadFloat(_addr);
                    if (float.IsNaN(cur) || Math.Abs(cur - RecoilScale) > 1e-6f)
                    {
                        if (WriteFloatAt(_addr, RecoilScale))
                            Logger.LogInformation("[VMRecoil] 卸载：常量已恢复为 {0:F3}（卸载前 {1}）", ReadFloat(_addr), Fmt(cur));
                        else
                            Logger.LogError("[VMRecoil] 卸载：常量恢复失败（卸载前 {0}），请重开游戏以还原后坐力", Fmt(cur));
                    }
                    else
                    {
                        Logger.LogInformation("[VMRecoil] 卸载：常量已是原值 {0:F3}，无需恢复", cur);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[VMRecoil] 卸载恢复异常：{0}: {1}", ex.GetType().Name, ex.Message);
            }

            Logger.LogInformation("[VMRecoil] 已卸载，用户设置保留在 {0}", StatePath());
        }

        // ------------------------------------------------------------ cvar 创建
        private void EnsureCvar()
        {
            try
            {
                _existingCvar = ConVar.Find(CvarName);
                if (_existingCvar != null)
                {
                    Logger.LogInformation("[VMRecoil] cvar {0} 已存在（热重载 / 上次会话遗留），复用不重复注册", CvarName);
                    return;
                }
            }
            catch { }

            IntPtr baseAddr = ClientModuleBase();
            if (baseAddr == IntPtr.Zero) { Logger.LogError("[VMRecoil] client.dll 基址不可用，cvar 无法创建"); return; }

            var loc = new AobLocator(baseAddr);
            if (!loc.MapText()) { Logger.LogError("[VMRecoil] client.dll .text 映射失败，cvar 无法创建"); return; }
            loc.ScanAll();

            int rInit = loc.Rva("cvar.refinit");
            int rReg = loc.Rva("cvar.regcore");
            int rGet = loc.Rva("cvar.getfloat");
            if (rInit <= 0 || rReg <= 0 || rGet <= 0)
            {
                Logger.LogError("[VMRecoil] cvar 注册链签名不可用（refinit={0} regcore={1} getfloat={2}），cvar 未创建；" +
                                "为避免控制台补全闪退，本插件不注册任何 ConCommand。", rInit, rReg, rGet);
                foreach (SigResult s in loc.All)
                    Logger.LogInformation("[VMRecoil] sig {0}: {1}", s.Name, loc.Describe(s));
                return;
            }

            var initFn = Marshal.GetDelegateForFunctionPointer<ConVarRefInitFn>(baseAddr + rInit);
            var regFn = Marshal.GetDelegateForFunctionPointer<RegCoreFn>(baseAddr + rReg);
            _getFloat = Marshal.GetDelegateForFunctionPointer<GetFloatFn>(baseAddr + rGet);

            // 注意：以下非托管内存在进程生命周期内常驻，原因见类注释（引擎 cvar 表持有引用）。
            _cvarRef = Marshal.AllocHGlobal(0x10);
            for (int k = 0; k < 0x10; k++) Marshal.WriteByte(_cvarRef, k, 0);
            _cvarNamePtr = Marshal.StringToHGlobalAnsi(CvarName);
            _cvarHelpPtr = Marshal.StringToHGlobalAnsi("viewmodel recoil: 1=on(default) 0=off (viewmodel stays still)");
            initFn(_cvarRef, -1, 7);

            _cvarDescPtr = Marshal.AllocHGlobal(0x48);
            for (int k = 0; k < 0x48; k++) Marshal.WriteByte(_cvarDescPtr, k, 0);
            WriteF(_cvarDescPtr, 0x07, 1f);            // 默认值
            Marshal.WriteByte(_cvarDescPtr, 0x04, 1);  // 有默认值
            WriteF(_cvarDescPtr, 0x17, 0f);            // 最小值
            Marshal.WriteByte(_cvarDescPtr, 0x05, 1);
            WriteF(_cvarDescPtr, 0x27, 1f);            // 最大值
            Marshal.WriteByte(_cvarDescPtr, 0x06, 1);
            Marshal.WriteInt16(_cvarDescPtr, 0x40, 7); // 类型 = float

            // FCVAR_CLIENTDLL(0x8) | FCVAR_ARCHIVE(0x80)：与 BobCore 验证过的取值一致
            const ulong flags = 0x8UL | 0x80UL;
            regFn(_cvarRef, _cvarNamePtr, flags, _cvarHelpPtr, _cvarDescPtr, IntPtr.Zero);

            float got = ReadCvarValue();
            Logger.LogInformation("[VMRecoil] cvar {0} 注册完成 读回={1}{2}",
                CvarName, Fmt(got),
                float.IsNaN(got) ? "  → 注册失败（ref+8 为空）" : "");
        }

        private void RefreshCssHandle()
        {
            try { _existingCvar = ConVar.Find(CvarName); } catch { }
        }

        /// <summary>
        /// 读当前 cvar 值。优先走 CSS 的 ConVar.Find（引擎 cvar 表里的真实值，控制台写入的就是它），
        /// 读不到再退回自建 ref+8 的 GetFloat。两条通道都读不到返回 NaN。
        /// </summary>
        private float ReadCvarValue(out string channel)
        {
            channel = "none";

            try
            {
                if (_existingCvar == null) _existingCvar = ConVar.Find(CvarName);
                if (_existingCvar != null)
                {
                    // float cvar 必须走 GetPrimitiveValue<float>()，直接取 StringValue 会抛类型异常
                    float v = _existingCvar.GetPrimitiveValue<float>();
                    channel = "css";
                    return v;
                }
            }
            catch (Exception ex) { _lastCssError = ex.GetType().Name + ": " + ex.Message; }

            try
            {
                if (_cvarRef != IntPtr.Zero && Marshal.ReadInt64(_cvarRef, 8) != 0 && _getFloat != null)
                {
                    channel = "ref8";
                    return _getFloat(_cvarRef);
                }
            }
            catch { }

            return float.NaN;
        }

        private float ReadCvarValue() => ReadCvarValue(out _);

        // ------------------------------------------------------------ 只读探针
        /// <summary>引擎侧可见性检查：只读，绝不写 cvar（v2.3.0 的自检写入会覆盖用户 OFF 设置，已删）。</summary>
        private void Probe()
        {
            RefreshCssHandle();
            bool visible = _existingCvar != null;
            float cur = ReadCvarValue(out string ch);
            Logger.LogInformation("[VMRecoil] 可见性检查：ConVar.Find({0})={1}，ref+8={2}，读值={3}（通道={4}）{5}",
                CvarName, visible ? "命中" : "为空",
                _cvarRef == IntPtr.Zero ? "无" : (Marshal.ReadInt64(_cvarRef, 8) != 0 ? "有" : "空"),
                Fmt(cur), ch,
                string.IsNullOrEmpty(_lastCssError) ? "" : "，css通道异常=" + _lastCssError);

            if (!visible)
                Logger.LogWarning("[VMRecoil] cvar 未进入引擎 cvar 表 → 控制台敲 {0} 会被当作未知命令", CvarName);
        }

        // ------------------------------------------------------------ 轮询
        private void Poll()
        {
            if (_unloaded || _addr == IntPtr.Zero) return;

            _pollCount++;
            float v = ReadCvarValue(out string ch);

            if (float.IsNaN(v))
            {
                _nanStreak++;
                if (_nanStreak == 20 || _nanStreak % 300 == 0)
                    Logger.LogWarning("[VMRecoil] cvar 连续 {0} 次读不到值（通道={1}）{2}",
                        _nanStreak, ch, string.IsNullOrEmpty(_lastCssError) ? "" : "，css异常=" + _lastCssError);
                RefreshCssHandle();
                return;
            }
            if (_nanStreak > 0)
            {
                Logger.LogInformation("[VMRecoil] cvar 读取恢复（此前连续 {0} 次失败）", _nanStreak);
                _nanStreak = 0;
            }

            // 待写回的用户设置：每秒重试一次，最多 10 次
            if (!float.IsNaN(_pendingRestore))
            {
                if (Math.Abs(v - _pendingRestore) < 1e-3f)
                {
                    Logger.LogInformation("[VMRecoil] 用户设置已写回 cvar：{0}", Fmt(v));
                    _pendingRestore = float.NaN;
                    _restoreAttempts = 0;
                    _restoreTicks = 0;
                }
                else if (++_restoreTicks >= 10)
                {
                    _restoreTicks = 0;
                    if (_restoreAttempts < 10)
                    {
                        _restoreAttempts++;
                        Logger.LogWarning("[VMRecoil] 用户设置 {0} 尚未生效（当前 {1}），第 {2} 次重写",
                            Fmt(_pendingRestore), Fmt(v), _restoreAttempts);
                        IssueRestore(_pendingRestore);
                    }
                    else if (_restoreAttempts == 10)
                    {
                        _restoreAttempts++;
                        Logger.LogError("[VMRecoil] 用户设置 {0} 写回失败已放弃（cvar 不可写）；内存常量仍按 cvar={1} 处理，设置文件保留",
                            Fmt(_pendingRestore), Fmt(v));
                        _pendingRestore = float.NaN;
                    }
                }
            }

            // cvar 值变化日志
            if (float.IsNaN(_lastSeenValue) || Math.Abs(v - _lastSeenValue) > 1e-4f)
            {
                Logger.LogInformation("[VMRecoil] cvar 值变化：{0} → {1}（通道={2}）",
                    Fmt(_lastSeenValue), Fmt(v), ch);
                _lastSeenValue = v;
            }

            // 以 cvar 为唯一真值同步常量（同时覆盖"状态切换"和"常量被别处改坏"的漂移自愈）
            int on = v >= 0.5f ? 1 : 0;
            float expected = Expected(on);
            float actual = ReadFloat(_addr);
            if (float.IsNaN(actual) || Math.Abs(actual - expected) > 1e-5f)
                Apply(on, "cvar=" + Fmt(v));
            else if (_state != on)
                _state = on;

            // 变化即落盘（用户改过 cvar 就保存）
            SaveValue(v);
        }

        private void Heartbeat()
        {
            if (_unloaded || _addr == IntPtr.Zero) return;
            float v = ReadCvarValue(out string ch);
            Logger.LogInformation("[VMRecoil] 心跳 cvar={0}（{1}）常量={2:F3} state={3} 设置={4}",
                Fmt(v), ch, ReadFloat(_addr), _state, Fmt(_lastSavedValue));
        }

        // ------------------------------------------------------------ 用户设置持久化
        private static string PluginDir()
        {
            try
            {
                string loc = typeof(VMRecoilPlugin).Assembly.Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    string dir = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
            }
            catch { }
            return AppContext.BaseDirectory;
        }

        private static string StatePath() => Path.Combine(PluginDir(), StateFileName);

        /// <summary>读用户设置；文件不存在 / 解析失败返回 NaN（调用方按默认 1 处理）。</summary>
        private float LoadSavedValue()
        {
            string path = StatePath();
            try
            {
                if (!File.Exists(path))
                {
                    Logger.LogInformation("[VMRecoil] 未找到用户设置文件 {0}（首次运行）", path);
                    return float.NaN;
                }

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                    string[] parts = line.Split(new char[] { ' ', '\t', '=' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    if (!string.Equals(parts[0], CvarName, StringComparison.OrdinalIgnoreCase)) continue;

                    if (float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                    {
                        float clamped = val < 0f ? 0f : (val > 1f ? 1f : val);
                        if (Math.Abs(clamped - val) > 1e-6f)
                            Logger.LogWarning("[VMRecoil] 设置文件中的值 {0} 超出 [0,1]，已按 {1} 处理", val, clamped);
                        Logger.LogInformation("[VMRecoil] 读取用户设置 {0} = {1}（来自 {2}）", CvarName, Fmt(clamped), path);
                        return clamped;
                    }
                    Logger.LogWarning("[VMRecoil] 设置文件中的值无法解析：{0}", line);
                }

                Logger.LogWarning("[VMRecoil] 设置文件 {0} 中没有 {1} 的有效行，按首次运行处理", path, CvarName);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[VMRecoil] 读取用户设置失败：{0}: {1}（按首次运行处理）", ex.GetType().Name, ex.Message);
            }
            return float.NaN;
        }

        /// <summary>值变化时原子落盘（写临时文件再替换），失败只告警不影响运行。</summary>
        private void SaveValue(float v)
        {
            if (float.IsNaN(v)) return;
            if (!float.IsNaN(_lastSavedValue) && Math.Abs(_lastSavedValue - v) < 1e-6f) return;

            string path = StatePath();
            string tmp = path + ".tmp";
            try
            {
                string text =
                    "# VMRecoil 用户设置（插件自动生成，可直接编辑）\r\n" +
                    "# 修改后重载插件或用控制台改 cvar 均会覆盖本文件\r\n" +
                    CvarName + " " + v.ToString("0.###", CultureInfo.InvariantCulture) + "\r\n";

                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);

                _lastSavedValue = v;
                Logger.LogInformation("[VMRecoil] 已保存用户设置：{0} {1} → {2}", CvarName, Fmt(v), path);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[VMRecoil] 保存用户设置失败：{0}: {1}（不影响当前生效值）", ex.GetType().Name, ex.Message);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private void BeginRestore(float value)
        {
            _pendingRestore = value;
            _restoreTicks = 0;
            _restoreAttempts = 0;
            IssueRestore(value);
        }

        private void IssueRestore(float value)
        {
            try
            {
                Server.ExecuteCommand(string.Format(CultureInfo.InvariantCulture, "{0} {1:0.###}", CvarName, value));
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[VMRecoil] 写回 cvar 失败：{0}: {1}", ex.GetType().Name, ex.Message);
            }
        }

        // ------------------------------------------------------------ 写入常量
        private static float Expected(int on) => on == 0 ? 0f : RecoilScale;

        private bool Apply(int on, string reason)
        {
            if (_addr == IntPtr.Zero) return false;

            float want = Expected(on);
            if (!WriteFloatAt(_addr, want)) return false;

            float got = ReadFloat(_addr);
            if (float.IsNaN(got) || Math.Abs(got - want) > 1e-5f)
            {
                Logger.LogWarning("[VMRecoil] 写入回读不一致（期望 {0:F6}，实得 {1}），重试一次", want, Fmt(got));
                if (!WriteFloatAt(_addr, want)) return false;
                got = ReadFloat(_addr);
                if (float.IsNaN(got) || Math.Abs(got - want) > 1e-5f)
                {
                    Logger.LogError("[VMRecoil] 写入常量失败：回读 {0}，state 保持 {1}", Fmt(got), _state);
                    return false;
                }
            }

            bool changed = _state != on;
            _state = on;
            Logger.LogInformation("[VMRecoil] {0} → 常量 {1:F3}（{2}）{3}",
                on == 0 ? "OFF" : "ON", got, changed ? "状态切换" : "自愈修复",
                string.IsNullOrEmpty(reason) ? "" : "，原因 " + reason);
            return true;
        }

        /// <summary>整页可写保护后写 4 字节，再还原保护属性；避免 4 字节跨页时保护范围不足。</summary>
        private bool WriteFloatAt(IntPtr addr, float value)
        {
            const long pageMask = ~0xFFFL;
            long start = addr.ToInt64() & pageMask;
            long end = addr.ToInt64() + 4;
            uint size = (uint)(end - start);

            if (!VirtualProtect((IntPtr)start, (UIntPtr)size, PageExecuteReadWrite, out uint oldProtect))
            {
                Logger.LogError("[VMRecoil] VirtualProtect(RWX) 失败 addr={0:X} code={1}", addr.ToInt64(), Marshal.GetLastWin32Error());
                return false;
            }

            try
            {
                Marshal.WriteInt32(addr, BitConverter.SingleToInt32Bits(value));
            }
            catch (Exception ex)
            {
                Logger.LogError("[VMRecoil] 写入内存异常：{0}: {1}", ex.GetType().Name, ex.Message);
                return false;
            }
            finally
            {
                VirtualProtect((IntPtr)start, (UIntPtr)size, oldProtect, out _);
            }
            return true;
        }

        // ------------------------------------------------------------ 定位
        private static IntPtr ClientModuleBase()
        {
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            {
                if (string.Equals(m.ModuleName, "client.dll", StringComparison.OrdinalIgnoreCase))
                    return m.BaseAddress;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 三重校验定位：
        ///   1) RVA 直读，值必须为 0.325f 才直接采信；
        ///   2) 值为 0.0f（上次会话可能被写成 0）时，必须由签名扫描反算出"同一地址"才采信；
        ///   3) 签名命中其它地址时，该地址值必须为 0.325f 才采信。
        /// 任何一条不满足都返回 Zero，宁可不改也不误改无关常量。
        /// </summary>
        private IntPtr Locate()
        {
            ProcessModule mod = null;
            try
            {
                foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                {
                    if (string.Equals(m.ModuleName, "client.dll", StringComparison.OrdinalIgnoreCase)) { mod = m; break; }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[VMRecoil] 枚举进程模块失败：{0}", ex.Message);
                return IntPtr.Zero;
            }

            if (mod == null)
            {
                Logger.LogError("[VMRecoil] 进程中没有 client.dll。");
                return IntPtr.Zero;
            }

            IntPtr baseAddr = mod.BaseAddress;
            int size = mod.ModuleMemorySize;
            IntPtr direct = IntPtr.Add(baseAddr, ConstRva);
            float dv = ReadFloat(direct);

            if (Math.Abs(dv - RecoilScale) < 1e-4f)
            {
                Logger.LogInformation("[VMRecoil] RVA 命中并校验通过：base={0:X} size={1:X} 值={2:F6}",
                    baseAddr.ToInt64(), size, dv);
                return direct;
            }

            Logger.LogWarning("[VMRecoil] RVA 直读值异常（{0}），转入签名校验。", Fmt(dv));
            IntPtr cand = ScanSignature(baseAddr, size);

            if (cand == IntPtr.Zero)
            {
                Logger.LogError("[VMRecoil] 签名扫描无有效命中，放弃定位（不改动任何内存）。");
                return IntPtr.Zero;
            }

            if (cand == direct)
            {
                Logger.LogInformation("[VMRecoil] 签名反算地址与 RVA 一致（{0:X}），值 {1} 视为上次会话遗留，采信",
                    cand.ToInt64(), Fmt(ReadFloat(cand)));
                return cand;
            }

            float cv = ReadFloat(cand);
            if (Math.Abs(cv - RecoilScale) < 1e-4f)
            {
                Logger.LogInformation("[VMRecoil] 签名命中并校验通过：addr={0:X} 值={1:F6}", cand.ToInt64(), cv);
                return cand;
            }

            Logger.LogError("[VMRecoil] 签名命中地址 {0:X} 的值 {1} 不符（需 0.325），放弃定位。", cand.ToInt64(), Fmt(cv));
            return IntPtr.Zero;
        }

        private IntPtr ScanSignature(IntPtr baseAddr, int size)
        {
            byte[] buf;
            try
            {
                buf = new byte[size];
                Marshal.Copy(baseAddr, buf, 0, size);
            }
            catch (Exception ex)
            {
                Logger.LogError("[VMRecoil] 读取模块内存失败：{0}", ex.Message);
                return IntPtr.Zero;
            }

            // call rel32 ; movss xmm2,[rip+disp32] ; lea rdx,[rsp+0x60] ; mov rcx,rax
            byte[] sig =
            {
                0xE8, 0, 0, 0, 0,
                0xF3, 0x0F, 0x10, 0x15, 0, 0, 0, 0,
                0x48, 0x8D, 0x54, 0x24, 0x60, 0x48, 0x8B, 0xC8
            };

            for (int i = 0; i + sig.Length <= buf.Length; i++)
            {
                if (buf[i] != sig[0]) continue;

                bool ok = true;
                for (int k = 0; k < sig.Length; k++)
                {
                    if (k >= 1 && k <= 4) continue;     // call rel32 通配
                    if (k >= 9 && k <= 12) continue;    // rip disp32 通配
                    if (buf[i + k] != sig[k]) { ok = false; break; }
                }
                if (!ok) continue;

                int disp = BitConverter.ToInt32(buf, i + 9);
                long target = (long)i + 13 + disp;
                if (target < 0 || target + 4 > buf.Length) continue;

                IntPtr cand = IntPtr.Add(baseAddr, (int)target);
                Logger.LogInformation("[VMRecoil] 签名命中 fileoff={0:X} -> addr={1:X}",
                    i, cand.ToInt64());
                return cand;
            }

            return IntPtr.Zero;
        }

        private static float ReadFloat(IntPtr addr)
        {
            try
            {
                if (addr == IntPtr.Zero) return float.NaN;
                return BitConverter.Int32BitsToSingle(Marshal.ReadInt32(addr));
            }
            catch { return float.NaN; }
        }

        private static string Fmt(float f) => float.IsNaN(f) ? "NaN" : f.ToString("0.###", CultureInfo.InvariantCulture);

        private static void WriteF(IntPtr p, int off, float v)
        {
            Marshal.Copy(BitConverter.GetBytes(v), 0, p + off, 4);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);
    }
}
