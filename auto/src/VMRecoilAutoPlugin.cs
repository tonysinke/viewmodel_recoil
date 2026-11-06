// ============================================================================
//  VMRecoilAuto — v3.0.0（长期做法版）
// ----------------------------------------------------------------------------
//  和 v2.x 的区别只有一处：**不再把任何版本地址写死在代码里**。
//
//    v2.x：常量 RVA 0x12E11BC 硬编码 + 一条 22 字节 AOB；client.dll 一更新就失效
//    v3.0：常量靠"值指纹 + 引用唯一性"自动找；cvar 三件套签名放进外部 JSON
//
//  功能与 v2.4.0 完全一致：
//    1) 把 client.dll 控制 viewmodel 后坐力缩放的 float（默认 0.325f）改成 0.0f / 改回来；
//    2) 注册真实 float cvar（默认 viewmodel_recoil），走引擎 cvar 表，控制台可见可改；
//    3) 用户设置自动落盘，换图 / 重载 / 重开游戏自动恢复；
//    4) 卸载时把常量恢复成原值，不留"后坐力永久消失"的坑。
//
//  长期版的自我克制（安全闸门，写在代码里、不可配置关闭）：
//    · 候选地址的当前值必须落在 onValue±tol 或 offValue±tol，否则一个字节都不写；
//    · 常量必须唯一命中（默认）、指向它的 movss 必须唯一（默认）；
//    · 任何一步不满足 → 空转 + 明确日志，绝不"猜个地址试试"。
//
//  热重载自证：上次定位到的 RVA 会被写进状态文件，下次 Load 先验值再用，
//  这样即使常量此刻被改成了 0.0f（值指纹暂时搜不到 0.325），也能接上。
// ============================================================================

using System;
using System.Collections.Generic;
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

namespace VMRecoilAuto
{
    public class VMRecoilAutoPlugin : BasePlugin
    {
        public override string ModuleName => "VMRecoilAuto";
        public override string ModuleVersion => "3.0.0";
        public override string ModuleAuthor => "tonysinke";
        public override string ModuleDescription => "viewmodel_recoil as a real cvar, located automatically (no per-version hardcoded RVA)";

        private const uint PageExecuteReadWrite = 0x40;
        private const string StateFileName = "vmrecoil_auto_user.cfg";
        private const string StateKeyConstRva = "const_rva";

        // cvar 注册链（参数布局与 v2.x 一致，已在 1.40.4.5 实机验证）
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ConVarRefInitFn(IntPtr refPtr, int unk1, short idx);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RegCoreFn(IntPtr refPtr, IntPtr name, ulong flags,
                                        IntPtr help, IntPtr desc, IntPtr unused);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate float GetFloatFn(IntPtr refPtr);

        // ------------------------------------------------------------ 配置与定位状态
        private AutoConfig _cfg;
        private string _cfgPath = "";

        private IntPtr _modBase = IntPtr.Zero;
        private int _imageSize;
        private AutoLocator _loc;
        private int _savedConstRva = -1;    // 上次成功定位的 RVA（来自状态文件）
        private string _locateReport = "未定位";

        private IntPtr _addr = IntPtr.Zero;
        private int _state = 1;             // 1 = 原版值，0 = 关闭值
        private bool _unloaded;

        // ------------------------------------------------------------ 运行期对象
        private IntPtr _cvarRef = IntPtr.Zero;
        private IntPtr _cvarNamePtr = IntPtr.Zero;
        private IntPtr _cvarHelpPtr = IntPtr.Zero;
        private IntPtr _cvarDescPtr = IntPtr.Zero;
        private GetFloatFn _getFloat;
        private ConVar _existingCvar;

        private string _lastCssError = "";
        private float _lastSeenValue = float.NaN;
        private float _lastSavedValue = float.NaN;
        private float _pendingRestore = float.NaN;
        private int _restoreTicks;
        private int _restoreAttempts;
        private int _nanStreak;

        private string CvarName { get { return _cfg != null ? _cfg.CvarName : "viewmodel_recoil"; } }
        private float OnValue { get { return _cfg != null ? _cfg.OnValue : 0.325f; } }
        private float OffValue { get { return _cfg != null ? _cfg.OffValue : 0f; } }
        private float Tolerance { get { return _cfg != null ? _cfg.ValueTolerance : 1e-4f; } }

        // ------------------------------------------------------------ Load / Unload
        public override void Load(bool hotReload)
        {
            try
            {
                LoadCore(hotReload);
            }
            catch (Exception ex)
            {
                Logger.LogError("[VMRecoilAuto] Load 异常，本次空转（不改动任何内存）：{0}: {1}", ex.GetType().Name, ex.Message);
            }
        }

        private void LoadCore(bool hotReload)
        {
            _unloaded = false;

            // ---- 1. 外部配置
            _cfgPath = Path.Combine(PluginDir(), AutoConfig.FileName);
            _cfg = AutoConfig.LoadOrCreate(_cfgPath);
            foreach (string n in _cfg.Notes) Logger.LogInformation("[VMRecoilAuto] 配置：{0}", n);
            Logger.LogInformation("[VMRecoilAuto] 生效参数：{0}", _cfg.Summary());

            if (Math.Abs(OnValue - OffValue) < Tolerance)
            {
                Logger.LogError("[VMRecoilAuto] 配置里 onValue({0}) 与 offValue({1}) 太接近，开关无意义；本次空转", OnValue, OffValue);
                return;
            }

            // ---- 2. 读状态文件（用户设置 + 上次定位结果）
            _savedConstRva = -1;
            float saved = LoadSavedValue(out _savedConstRva);
            _lastSavedValue = saved;

            // ---- 3. 定位常量
            _addr = Locate();

            if (_addr == IntPtr.Zero)
            {
                Logger.LogError("[VMRecoilAuto] 未能定位 viewmodel 后坐力常量，本次不会改动任何内存。" +
                                "定位报告：{0}", _locateReport);
            }
            else
            {
                float cur = ReadFloat(_addr);
                _state = Math.Abs(cur - OnValue) <= Tolerance ? 1 : 0;
                Logger.LogInformation("[VMRecoilAuto] 常量已定位 addr={0:X} RVA=0x{1:X} 当前值={2:F6} state={3}",
                    _addr.ToInt64(), _addr.ToInt64() - _modBase.ToInt64(), cur, _state);
            }

            // ---- 4. cvar
            EnsureCvar();
            RefreshCssHandle();

            float cv = ReadCvarValue(out string ch0);
            Logger.LogInformation("[VMRecoilAuto] 起始读值 {0}（通道={1}）", Fmt(cv), ch0);

            float target;
            if (!float.IsNaN(saved))
            {
                target = saved;
                if (!float.IsNaN(cv) && Math.Abs(cv - saved) < 1e-3f)
                {
                    Logger.LogInformation("[VMRecoilAuto] cvar 当前值已是用户设置 {0}，无需写回", Fmt(saved));
                }
                else if (!float.IsNaN(cv))
                {
                    Logger.LogInformation("[VMRecoilAuto] 恢复用户设置：{0}（当前 cvar={1}）", Fmt(saved), Fmt(cv));
                    BeginRestore(saved);
                }
                else
                {
                    Logger.LogWarning("[VMRecoilAuto] cvar 读不到值，暂无法写回用户设置 {0}；轮询期间会继续重试", Fmt(saved));
                    BeginRestore(saved);
                }
            }
            else
            {
                target = float.IsNaN(cv) ? 1f : cv;
                Logger.LogInformation("[VMRecoilAuto] 首次运行（无用户设置文件），按当前值 {0} 启动", Fmt(target));
            }

            if (_addr != IntPtr.Zero)
            {
                int want = target >= 0.5f ? 1 : 0;
                if (Math.Abs(ReadFloat(_addr) - Expected(want)) > 1e-5f) Apply(want, "Load");
                else _state = want;
            }

            AddTimer(1.0f, Probe);
            AddTimer(0.1f, Poll, TimerFlags.REPEAT);
            AddTimer(30.0f, Heartbeat, TimerFlags.REPEAT);

            Logger.LogInformation("[VMRecoilAuto] v{0} 就绪：{1} 为真实 cvar（cvarRef={2}），state={3}，设置文件={4}，"
                                  + "定位方式={5}。热重载={6}",
                ModuleVersion, CvarName, _cvarRef == IntPtr.Zero ? "find" : "self", _state, StatePath(), _locateReport,
                hotReload ? "是" : "否");
        }

        public override void Unload(bool hotReload)
        {
            _unloaded = true;
            try
            {
                if (_addr != IntPtr.Zero && _cfg != null)
                {
                    float cur = ReadFloat(_addr);
                    if (float.IsNaN(cur) || Math.Abs(cur - OnValue) > 1e-6f)
                    {
                        if (WriteFloatAt(_addr, OnValue))
                            Logger.LogInformation("[VMRecoilAuto] 卸载：常量已恢复为 {0:F3}（卸载前 {1}）", ReadFloat(_addr), Fmt(cur));
                        else
                            Logger.LogError("[VMRecoilAuto] 卸载：常量恢复失败（卸载前 {0}），请重开游戏以还原后坐力", Fmt(cur));
                    }
                    else
                    {
                        Logger.LogInformation("[VMRecoilAuto] 卸载：常量已是原值 {0:F3}，无需恢复", cur);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[VMRecoilAuto] 卸载恢复异常：{0}: {1}", ex.GetType().Name, ex.Message);
            }

            Logger.LogInformation("[VMRecoilAuto] 已卸载，用户设置保留在 {0}", StatePath());
        }

        // ------------------------------------------------------------ 定位（长期做法核心）
        /// <summary>
        /// 三级定位，全部经过"值合法"自证：
        ///   A) 状态文件里上次定位的 RVA —— 值合法就沿用（兼容热重载 / 常量当前为 offValue 的情况）
        ///   B) 配置里的 rvaHints —— 人工填的已知地址，值合法就采信（纯加速，可不填）
        ///   C) 值指纹 + 引用反查 —— 主力手段，跨版本有效
        /// </summary>
        private IntPtr Locate()
        {
            ProcessModule mod = FindClientModule();
            if (mod == null)
            {
                _locateReport = "进程中没有 client.dll";
                Logger.LogError("[VMRecoilAuto] {0}", _locateReport);
                return IntPtr.Zero;
            }

            _modBase = mod.BaseAddress;
            _imageSize = mod.ModuleMemorySize;
            Logger.LogInformation("[VMRecoilAuto] client.dll base=0x{0:X} 镜像大小={1}",
                _modBase.ToInt64(), AutoLocator.Fmt.Size(_imageSize));

            _loc = new AutoLocator(_modBase, _imageSize);
            if (!_loc.MapSections())
            {
                _locateReport = "解析 PE 节表失败";
                foreach (string n in _loc.Notes) Logger.LogError("[VMRecoilAuto] {0}", n);
                return IntPtr.Zero;
            }
            Logger.LogInformation("[VMRecoilAuto] 节表：{0}", _loc.DescribeSections());

            if (!_loc.MapText())
            {
                _locateReport = ".text 映射失败";
                foreach (string n in _loc.Notes) Logger.LogError("[VMRecoilAuto] {0}", n);
                return IntPtr.Zero;
            }

            // ---- A) 沿用上次定位结果
            if (_savedConstRva > 0 && _savedConstRva < _imageSize - 4)
            {
                IntPtr p = IntPtr.Add(_modBase, _savedConstRva);
                float v = ReadFloat(p);
                if (IsLegalValue(v))
                {
                    _locateReport = string.Format(CultureInfo.InvariantCulture,
                        "沿用上次定位（RVA 0x{0:X}，值 {1:F6}）", _savedConstRva, v);
                    Logger.LogInformation("[VMRecoilAuto] {0}", _locateReport);
                    return p;
                }
                Logger.LogWarning("[VMRecoilAuto] 状态文件记录的 RVA 0x{0:X} 当前值 {1} 已不合法（客户端可能更新了），改为自动定位",
                    _savedConstRva, Fmt(v));
            }

            // ---- B) 配置里的已知地址
            foreach (int rva in _cfg.RvaHints)
            {
                if (rva <= 0 || rva >= _imageSize - 4) continue;
                IntPtr p = IntPtr.Add(_modBase, rva);
                float v = ReadFloat(p);
                if (IsLegalValue(v))
                {
                    _locateReport = string.Format(CultureInfo.InvariantCulture, "配置 rvaHints 命中（RVA 0x{0:X}，值 {1:F6}）", rva, v);
                    Logger.LogInformation("[VMRecoilAuto] {0}", _locateReport);
                    SaveConstRva(rva);
                    return p;
                }
                Logger.LogWarning("[VMRecoilAuto] 配置 rvaHints 中的 0x{0:X} 值不合法（{1}），跳过", rva, Fmt(v));
            }

            // ---- C) 值指纹 + 引用反查
            return LocateByFingerprint();
        }

        private IntPtr LocateByFingerprint()
        {
            List<ConstantHit> cands = _loc.FindConstant(OnValue, Tolerance);
            Logger.LogInformation("[VMRecoilAuto] 值指纹：在镜像中搜索 {0}（容差 {1:0.####}）得到 {2} 个候选",
                Fmt(OnValue), Tolerance, cands.Count);

            if (cands.Count == 0)
            {
                _locateReport = "值指纹无命中";
                Logger.LogError("[VMRecoilAuto] 值指纹无命中：镜像里找不到 {0}。可能原因：客户端已改用别的数值 → " +
                                "把 vmrecoil_auto.json 里的 onValue 改成新值即可；或该常量被别的插件改写成了别的值。",
                    Fmt(OnValue));
                return IntPtr.Zero;
            }

            // 逐候选数引用（指向它的 movss 有几条）
            foreach (ConstantHit h in cands)
            {
                h.Refs = _loc.CountRefs(h.Rva, out h.FirstRefRva);
            }

            // requireUniqueReference = true  -> 常量必须恰好被 1 条 movss 读
            // requireUniqueReference = false -> 放宽为"至少被 1 条 movss 读"（该开关真实生效，不是摆设）
            bool needExactOneRef = _cfg.RequireUniqueReference;
            List<ConstantHit> unique = new List<ConstantHit>();
            foreach (ConstantHit h in cands)
            {
                bool refOk = needExactOneRef ? (h.Refs == 1) : (h.Refs >= 1);
                if (refOk && IsLegalValue(ReadFloat(IntPtr.Add(_modBase, h.Rva)))) unique.Add(h);
            }

            if (cands.Count == 1 && unique.Count == 1)
            {
                ConstantHit h = unique[0];
                _locateReport = string.Format(CultureInfo.InvariantCulture,
                    "值指纹唯一命中 + movss 引用唯一（RVA 0x{0:X}，引用指令 0x{1:X}，值 {2:F6}{3}）",
                    h.Rva, h.FirstRefRva, ReadFloat(IntPtr.Add(_modBase, h.Rva)), h.Exact ? "" : "，容差匹配");
                Logger.LogInformation("[VMRecoilAuto] {0}", _locateReport);
                SaveConstRva(h.Rva);
                return IntPtr.Add(_modBase, h.Rva);
            }

            if (cands.Count > 1 && unique.Count == 1 && !_cfg.RequireUniqueConstant)
            {
                ConstantHit h = unique[0];
                _locateReport = string.Format(CultureInfo.InvariantCulture,
                    "多候选但仅 1 个被 movss 引用（RVA 0x{0:X}，共 {1} 个候选）", h.Rva, cands.Count);
                Logger.LogWarning("[VMRecoilAuto] {0}；配置已允许常量不唯一，采信该候选", _locateReport);
                SaveConstRva(h.Rva);
                return IntPtr.Add(_modBase, h.Rva);
            }

            // 单候选但结构闸门没过：最常见于"常量唯一、但被多条 movss 读"
            if (cands.Count == 1)
            {
                ConstantHit only = cands[0];
                if (only.Refs == 0)
                {
                    _locateReport = string.Format(CultureInfo.InvariantCulture,
                        "值指纹唯一命中（RVA 0x{0:X}），但没有任何 movss 读它", only.Rva);
                    Logger.LogError("[VMRecoilAuto] {0}", _locateReport);
                    Logger.LogError("[VMRecoilAuto] 已放弃改动，一个字节都没写。该 float 大概率不是目标常量，别硬写。");
                }
                else if (!IsLegalValue(ReadFloat(IntPtr.Add(_modBase, only.Rva))))
                {
                    _locateReport = string.Format(CultureInfo.InvariantCulture,
                        "值指纹唯一命中（RVA 0x{0:X}），但此刻读到的值 {1:F6} 既不等于 onValue 也不等于 offValue",
                        only.Rva, ReadFloat(IntPtr.Add(_modBase, only.Rva)));
                    Logger.LogError("[VMRecoilAuto] {0}", _locateReport);
                    Logger.LogError("[VMRecoilAuto] 已放弃改动，一个字节都没写。多半是别的插件/工具正在改这个常量，" +
                                    "或 onValue 与实际值已不符。");
                }
                else
                {
                    _locateReport = string.Format(CultureInfo.InvariantCulture,
                        "值指纹唯一命中（RVA 0x{0:X}），但读到它的 movss 有 {1} 条（requireUniqueReference={2}）",
                        only.Rva, only.Refs,
                        _cfg.RequireUniqueReference ? "true，要求恰好 1 条" : "false，要求 >=1 条");
                    Logger.LogError("[VMRecoilAuto] {0}", _locateReport);
                    Logger.LogError("[VMRecoilAuto] 已放弃改动，一个字节都没写。确认这条就是目标常量后，" +
                                    "可把 vmrecoil_auto.json 里的 requireUniqueReference 设为 false（不推荐），" +
                                    "或把该 RVA 写进 rvaHints（仍会验值）。");
                }
                return IntPtr.Zero;
            }

            // 走到这里说明指纹不够干净：宁可不动内存
            StringBuilder sb = new StringBuilder();
            int shown = 0;
            foreach (ConstantHit h in cands)
            {
                if (shown++ >= 12) { sb.Append(" …"); break; }
                if (sb.Length > 0) sb.Append("; ");
                sb.AppendFormat(CultureInfo.InvariantCulture, "RVA 0x{0:X} 引用 {1} 条", h.Rva, h.Refs);
            }

            _locateReport = string.Format(CultureInfo.InvariantCulture,
                "指纹不唯一，主动放弃（候选 {0} 个，其中被唯一引用的 {1} 个）：{2}", cands.Count, unique.Count, sb);

            Logger.LogError("[VMRecoilAuto] {0}", _locateReport);
            Logger.LogError("[VMRecoilAuto] 已放弃改动，一个字节都没写。若确认要压过这项保护，把 vmrecoil_auto.json 里的 " +
                            "requireUniqueConstant / requireUniqueReference 设为 false 再试（不推荐）。");
            return IntPtr.Zero;
        }

        /// <summary>值是否"合法"：等于 onValue 或 offValue（含容差）。</summary>
        private bool IsLegalValue(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return false;
            if (Math.Abs(v - OnValue) <= Tolerance) return true;
            if (Math.Abs(v - OffValue) <= Tolerance) return true;
            return false;
        }

        private static ProcessModule FindClientModule()
        {
            try
            {
                foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                {
                    if (string.Equals(m.ModuleName, "client.dll", StringComparison.OrdinalIgnoreCase))
                        return m;
                }
            }
            catch { }
            return null;
        }

        private static IntPtr ClientModuleBase()
        {
            ProcessModule m = FindClientModule();
            return m == null ? IntPtr.Zero : m.BaseAddress;
        }

        // ------------------------------------------------------------ cvar 创建
        private void EnsureCvar()
        {
            try
            {
                _existingCvar = ConVar.Find(CvarName);
                if (_existingCvar != null)
                {
                    Logger.LogInformation("[VMRecoilAuto] cvar {0} 已存在（热重载 / 上次会话遗留），复用不重复注册", CvarName);
                    return;
                }
            }
            catch { }

            if (_modBase == IntPtr.Zero)
            {
                ProcessModule m = FindClientModule();
                if (m == null) { Logger.LogError("[VMRecoilAuto] client.dll 基址不可用，cvar 无法创建"); return; }
                _modBase = m.BaseAddress;
                _imageSize = m.ModuleMemorySize;
            }

            if (_loc == null)
            {
                _loc = new AutoLocator(_modBase, _imageSize);
                _loc.MapSections();
            }
            if (!_loc.Mapped && !_loc.MapText())
            {
                Logger.LogError("[VMRecoilAuto] client.dll .text 不可用，cvar 无法创建（常量改写功能不受影响）");
                return;
            }

            // ---- 三件套签名：全部来自外部配置
            SigHit hInit = _loc.ScanSig("refinit", GetSig("refinit"), GetBaseline("refinit"));
            SigHit hReg = _loc.ScanSig("regcore", GetSig("regcore"), GetBaseline("regcore"));
            SigHit hGet = _loc.ScanSig("getfloat", GetSig("getfloat"), GetBaseline("getfloat"));

            foreach (SigHit h in new SigHit[] { hInit, hReg, hGet })
                Logger.LogInformation("[VMRecoilAuto] 签名 {0}: {1}", h.Name, h.Reason);

            bool getOptional = IsOptional("getfloat");

            if (!hInit.Unique || !hReg.Unique || (!getOptional && !hGet.Unique))
            {
                Logger.LogError("[VMRecoilAuto] cvar 注册链签名不可用（refinit={0} regcore={1} getfloat={2}），cvar 未创建；" +
                                "为避免控制台补全闪退，本插件不注册任何 ConCommand。",
                    hInit.Unique ? "ok" : "失效", hReg.Unique ? "ok" : "失效",
                    hGet.Unique ? "ok" : (getOptional ? "可选，已跳过" : "失效"));
                Logger.LogError("[VMRecoilAuto] 处理办法：在 {0} 的 cvarSignatures 里更新对应 pattern（不用重新编译），" +
                                "重载插件即可；常量改写功能不受影响。", _cfgPath);
                return;
            }

            IntPtr baseAddr = _modBase;
            var initFn = Marshal.GetDelegateForFunctionPointer<ConVarRefInitFn>(baseAddr + hInit.FirstHitRva);
            var regFn = Marshal.GetDelegateForFunctionPointer<RegCoreFn>(baseAddr + hReg.FirstHitRva);
            _getFloat = hGet.Unique
                ? Marshal.GetDelegateForFunctionPointer<GetFloatFn>(baseAddr + hGet.FirstHitRva)
                : null;

            if (_getFloat == null)
                Logger.LogWarning("[VMRecoilAuto] getfloat 通道不可用（签名不唯一或被关掉）→ 只用 CounterStrikeSharp 的 ConVar 通道读值，" +
                                  "功能不受影响；这是预期内的降级，不需要处理。");

            // 以下非托管内存在进程生命周期内常驻（引擎 cvar 表持有引用，不能 Free）
            _cvarRef = Marshal.AllocHGlobal(0x10);
            for (int k = 0; k < 0x10; k++) Marshal.WriteByte(_cvarRef, k, 0);
            _cvarNamePtr = Marshal.StringToHGlobalAnsi(CvarName);
            _cvarHelpPtr = Marshal.StringToHGlobalAnsi("viewmodel recoil: 1=on(default) 0=off (viewmodel stays still)");
            initFn(_cvarRef, -1, 7);

            _cvarDescPtr = Marshal.AllocHGlobal(0x48);
            for (int k = 0; k < 0x48; k++) Marshal.WriteByte(_cvarDescPtr, k, 0);
            WriteF(_cvarDescPtr, 0x07, 1f);
            Marshal.WriteByte(_cvarDescPtr, 0x04, 1);
            WriteF(_cvarDescPtr, 0x17, 0f);
            Marshal.WriteByte(_cvarDescPtr, 0x05, 1);
            WriteF(_cvarDescPtr, 0x27, 1f);
            Marshal.WriteByte(_cvarDescPtr, 0x06, 1);
            Marshal.WriteInt16(_cvarDescPtr, 0x40, 7);

            const ulong flags = 0x8UL | 0x80UL;   // FCVAR_CLIENTDLL | FCVAR_ARCHIVE
            regFn(_cvarRef, _cvarNamePtr, flags, _cvarHelpPtr, _cvarDescPtr, IntPtr.Zero);

            float got = ReadCvarValue();
            Logger.LogInformation("[VMRecoilAuto] cvar {0} 注册完成 读回={1}{2}",
                CvarName, Fmt(got), float.IsNaN(got) ? "  → 注册失败（ref+8 为空）" : "");
        }

        private string GetSig(string name)
        {
            CvarSig s;
            return _cfg.Signatures.TryGetValue(name, out s) ? s.Pattern : null;
        }

        private int GetBaseline(string name)
        {
            CvarSig s;
            return _cfg.Signatures.TryGetValue(name, out s) ? s.BaselineRva : -1;
        }

        private bool IsOptional(string name)
        {
            CvarSig s;
            return _cfg.Signatures.TryGetValue(name, out s) && s.Optional;
        }

        private void RefreshCssHandle()
        {
            try { _existingCvar = ConVar.Find(CvarName); } catch { }
        }

        private float ReadCvarValue(out string channel)
        {
            channel = "none";

            try
            {
                if (_existingCvar == null) _existingCvar = ConVar.Find(CvarName);
                if (_existingCvar != null)
                {
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
        private void Probe()
        {
            RefreshCssHandle();
            bool visible = _existingCvar != null;
            float cur = ReadCvarValue(out string ch);
            Logger.LogInformation("[VMRecoilAuto] 可见性检查：ConVar.Find({0})={1}，ref+8={2}，读值={3}（通道={4}）{5}",
                CvarName, visible ? "命中" : "为空",
                _cvarRef == IntPtr.Zero ? "无" : (Marshal.ReadInt64(_cvarRef, 8) != 0 ? "有" : "空"),
                Fmt(cur), ch,
                string.IsNullOrEmpty(_lastCssError) ? "" : "，css通道异常=" + _lastCssError);

            if (!visible)
                Logger.LogWarning("[VMRecoilAuto] cvar 未进入引擎 cvar 表 → 控制台敲 {0} 会被当作未知命令", CvarName);
        }

        // ------------------------------------------------------------ 轮询
        private void Poll()
        {
            if (_unloaded || _addr == IntPtr.Zero) return;

            float v = ReadCvarValue(out string ch);

            if (float.IsNaN(v))
            {
                _nanStreak++;
                if (_nanStreak == 20 || _nanStreak % 300 == 0)
                    Logger.LogWarning("[VMRecoilAuto] cvar 连续 {0} 次读不到值（通道={1}）{2}",
                        _nanStreak, ch, string.IsNullOrEmpty(_lastCssError) ? "" : "，css异常=" + _lastCssError);
                RefreshCssHandle();
                return;
            }
            if (_nanStreak > 0)
            {
                Logger.LogInformation("[VMRecoilAuto] cvar 读取恢复（此前连续 {0} 次失败）", _nanStreak);
                _nanStreak = 0;
            }

            if (!float.IsNaN(_pendingRestore))
            {
                if (Math.Abs(v - _pendingRestore) < 1e-3f)
                {
                    Logger.LogInformation("[VMRecoilAuto] 用户设置已写回 cvar：{0}", Fmt(v));
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
                        Logger.LogWarning("[VMRecoilAuto] 用户设置 {0} 尚未生效（当前 {1}），第 {2} 次重写",
                            Fmt(_pendingRestore), Fmt(v), _restoreAttempts);
                        IssueRestore(_pendingRestore);
                    }
                    else if (_restoreAttempts == 10)
                    {
                        _restoreAttempts++;
                        Logger.LogError("[VMRecoilAuto] 用户设置 {0} 写回失败已放弃（cvar 不可写）；设置文件保留",
                            Fmt(_pendingRestore));
                        _pendingRestore = float.NaN;
                    }
                }
            }

            if (float.IsNaN(_lastSeenValue) || Math.Abs(v - _lastSeenValue) > 1e-4f)
            {
                Logger.LogInformation("[VMRecoilAuto] cvar 值变化：{0} → {1}（通道={2}）",
                    Fmt(_lastSeenValue), Fmt(v), ch);
                _lastSeenValue = v;
            }

            int on = v >= 0.5f ? 1 : 0;
            float expected = Expected(on);
            float actual = ReadFloat(_addr);
            if (float.IsNaN(actual) || Math.Abs(actual - expected) > 1e-5f)
                Apply(on, "cvar=" + Fmt(v));
            else if (_state != on)
                _state = on;

            SaveValue(v);
        }

        private void Heartbeat()
        {
            if (_unloaded || _addr == IntPtr.Zero) return;
            float v = ReadCvarValue(out string ch);
            Logger.LogInformation("[VMRecoilAuto] 心跳 cvar={0}（{1}）常量={2:F3} state={3} 设置={4} 定位={5}",
                Fmt(v), ch, ReadFloat(_addr), _state, Fmt(_lastSavedValue), _locateReport);
        }

        // ------------------------------------------------------------ 用户设置持久化
        private static string PluginDir()
        {
            try
            {
                string loc = typeof(VMRecoilAutoPlugin).Assembly.Location;
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

        /// <summary>读用户设置与上次定位 RVA。文件不存在 / 解析失败时值返回 NaN。</summary>
        private float LoadSavedValue(out int constRva)
        {
            constRva = -1;
            string path = StatePath();
            float value = float.NaN;

            try
            {
                if (!File.Exists(path))
                {
                    Logger.LogInformation("[VMRecoilAuto] 未找到用户设置文件 {0}（首次运行）", path);
                    return float.NaN;
                }

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                    string[] parts = line.Split(new char[] { ' ', '\t', '=' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    if (string.Equals(parts[0], StateKeyConstRva, StringComparison.OrdinalIgnoreCase))
                    {
                        constRva = ParseRva(parts[1]);
                        continue;
                    }

                    if (!string.Equals(parts[0], CvarName, StringComparison.OrdinalIgnoreCase)) continue;

                    if (float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                    {
                        float clamped = val < 0f ? 0f : (val > 1f ? 1f : val);
                        if (Math.Abs(clamped - val) > 1e-6f)
                            Logger.LogWarning("[VMRecoilAuto] 设置文件中的值 {0} 超出 [0,1]，已按 {1} 处理", val, clamped);
                        Logger.LogInformation("[VMRecoilAuto] 读取用户设置 {0} = {1}（来自 {2}）", CvarName, Fmt(clamped), path);
                        value = clamped;
                    }
                    else
                    {
                        Logger.LogWarning("[VMRecoilAuto] 设置文件中的值无法解析：{0}", line);
                    }
                }

                if (float.IsNaN(value))
                    Logger.LogWarning("[VMRecoilAuto] 设置文件 {0} 中没有 {1} 的有效行，按首次运行处理", path, CvarName);
                if (constRva > 0)
                    Logger.LogInformation("[VMRecoilAuto] 状态文件记录上次定位 RVA = 0x{0:X}", constRva);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[VMRecoilAuto] 读取用户设置失败：{0}: {1}（按首次运行处理）", ex.GetType().Name, ex.Message);
            }

            return value;
        }

        private static int ParseRva(string s)
        {
            try
            {
                if (string.IsNullOrEmpty(s)) return -1;
                s = s.Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hx) && hx > 0) return hx;
                    return -1;
                }
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dec) && dec > 0) return dec;
            }
            catch { }
            return -1;
        }

        /// <summary>定位成功后把 RVA 记进状态文件（单独一行，失败只告警）。</summary>
        private void SaveConstRva(int rva)
        {
            _savedConstRva = rva;
            try
            {
                WriteStateFile(_lastSavedValue, rva);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[VMRecoilAuto] 记录定位 RVA 失败：{0}: {1}（不影响当前运行）", ex.GetType().Name, ex.Message);
            }
        }

        private void SaveValue(float v)
        {
            if (float.IsNaN(v)) return;
            if (!float.IsNaN(_lastSavedValue) && Math.Abs(_lastSavedValue - v) < 1e-6f) return;

            try
            {
                WriteStateFile(v, _savedConstRva);
                _lastSavedValue = v;
                Logger.LogInformation("[VMRecoilAuto] 已保存用户设置：{0} {1} → {2}", CvarName, Fmt(v), StatePath());
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[VMRecoilAuto] 保存用户设置失败：{0}: {1}（不影响当前生效值）", ex.GetType().Name, ex.Message);
            }
        }

        private void WriteStateFile(float value, int constRva)
        {
            string path = StatePath();
            string tmp = path + ".tmp";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# VMRecoilAuto 运行状态（插件自动生成，可手工编辑）");
            sb.AppendLine("# 第一行是用户设置：改完重载插件或重开游戏即生效");
            sb.AppendLine("# 第二行是上次自动定位到的常量 RVA，仅用于下次快速定位；换版本后本插件会自动忽略过期值");
            sb.AppendLine(CvarName + " " + (float.IsNaN(value) ? "1" : value.ToString("0.###", CultureInfo.InvariantCulture)));
            if (constRva > 0) sb.AppendLine(StateKeyConstRva + " 0x" + constRva.ToString("X"));

            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
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
                Logger.LogWarning("[VMRecoilAuto] 写回 cvar 失败：{0}: {1}", ex.GetType().Name, ex.Message);
            }
        }

        // ------------------------------------------------------------ 写入常量
        private float Expected(int on) => on == 0 ? OffValue : OnValue;

        private bool Apply(int on, string reason)
        {
            if (_addr == IntPtr.Zero) return false;

            float want = Expected(on);
            if (!WriteFloatAt(_addr, want)) return false;

            float got = ReadFloat(_addr);
            if (float.IsNaN(got) || Math.Abs(got - want) > 1e-5f)
            {
                Logger.LogWarning("[VMRecoilAuto] 写入回读不一致（期望 {0:F6}，实得 {1}），重试一次", want, Fmt(got));
                if (!WriteFloatAt(_addr, want)) return false;
                got = ReadFloat(_addr);
                if (float.IsNaN(got) || Math.Abs(got - want) > 1e-5f)
                {
                    Logger.LogError("[VMRecoilAuto] 写入常量失败：回读 {0}，state 保持 {1}", Fmt(got), _state);
                    return false;
                }
            }

            bool changed = _state != on;
            _state = on;
            Logger.LogInformation("[VMRecoilAuto] {0} → 常量 {1:F3}（{2}）{3}",
                on == 0 ? "OFF" : "ON", got, changed ? "状态切换" : "自愈修复",
                string.IsNullOrEmpty(reason) ? "" : "，原因 " + reason);
            return true;
        }

        /// <summary>整页改可写后写 4 字节，再还原保护属性（避免 4 字节跨页时保护范围不足）。</summary>
        private bool WriteFloatAt(IntPtr addr, float value)
        {
            const long pageMask = ~0xFFFL;
            long start = addr.ToInt64() & pageMask;
            long end = addr.ToInt64() + 4;
            uint size = (uint)(end - start);

            if (!VirtualProtect((IntPtr)start, (UIntPtr)size, PageExecuteReadWrite, out uint oldProtect))
            {
                Logger.LogError("[VMRecoilAuto] VirtualProtect(RWX) 失败 addr={0:X} code={1}", addr.ToInt64(), Marshal.GetLastWin32Error());
                return false;
            }

            try
            {
                Marshal.WriteInt32(addr, BitConverter.SingleToInt32Bits(value));
            }
            catch (Exception ex)
            {
                Logger.LogError("[VMRecoilAuto] 写入内存异常：{0}: {1}", ex.GetType().Name, ex.Message);
                return false;
            }
            finally
            {
                VirtualProtect((IntPtr)start, (UIntPtr)size, oldProtect, out _);
            }
            return true;
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
