// ============================================================================
//  VMRecoil — AOB 定位层（精简版，仅保留 cvar 注册链）
// ----------------------------------------------------------------------------
//  用途：在 client.dll 的 .text 节按签名唯一定位引擎 cvar 注册三件套：
//        cvar.refinit  —— ConVarRef 初始化
//        cvar.regcore  —— 把 ref 注册进引擎 cvar 表（真正创建 cvar）
//        cvar.getfloat —— 读 ref 当前 float 值
//  签名取自 BobCore v1.36.0（同版本 client.dll 24.10.30 实测唯一命中）。
//  判定规则：全 .text 扫描必须恰好 1 处命中，否则该签名视为不可用，调用方关停对应功能。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace VMRecoil
{
    internal sealed class SigResult
    {
        public readonly string Name;
        public readonly int BaselineRva;
        public readonly byte[] Pat;
        public readonly byte[] Mask;

        public bool Scanned;
        public int Hits;
        public bool HitsCapped;
        public int FirstHitRva = -1;
        public int ResolvedRva = -1;
        public string Reason = "未扫描";

        public bool Unique { get { return Scanned && Hits == 1 && ResolvedRva > 0; } }

        public SigResult(string name, int baselineRva, string pattern)
        {
            Name = name;
            BaselineRva = baselineRva;

            string[] parts = pattern.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            Pat = new byte[parts.Length];
            Mask = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "??" || parts[i] == "?") { Pat[i] = 0; Mask[i] = 0; }
                else { Pat[i] = Convert.ToByte(parts[i], 16); Mask[i] = 1; }
            }
        }
    }

    internal sealed class AobLocator
    {
        public const int MaxHits = 16;

        private readonly IntPtr _base;
        private readonly List<SigResult> _sigs = new List<SigResult>();
        private readonly Dictionary<string, SigResult> _map = new Dictionary<string, SigResult>(StringComparer.Ordinal);

        private byte[] _text;
        private int _textRva;
        private int _textLen;

        public bool TextMapped { get { return _text != null; } }
        public IList<SigResult> All { get { return _sigs; } }

        public AobLocator(IntPtr baseAddr)
        {
            _base = baseAddr;
            BuildTable();
        }

        private void BuildTable()
        {
            // ---- cvar 注册链（三条都必须签名化，跨版本最易变）
            Add("cvar.refinit", 0x110EDB0, "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 B8 FF FF FF FF 49 0F");
            Add("cvar.regcore", 0x110FAE0, "48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 55 41 54 41 55 41 56 41 57 48 8D 6C 24 D1");
            Add("cvar.getfloat", 0x110DA0, "40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 48 85 C0 74 ?? F3 0F 10 00 48 83 C4 20 5B C3 48 8B 43 08 48 8B 48 08 F3 0F 10 01 48 83 C4 20 5B C3 CC 4C");
        }

        private void Add(string name, int baselineRva, string pattern)
        {
            SigResult s = new SigResult(name, baselineRva, pattern);
            _sigs.Add(s);
            _map[name] = s;
        }

        public SigResult Get(string name)
        {
            SigResult s;
            return _map.TryGetValue(name, out s) ? s : null;
        }

        /// <summary>取唯一命中反算出的 RVA；不可用时返回 -1（调用方必须按失败处理）。</summary>
        public int Rva(string name)
        {
            SigResult s = Get(name);
            return (s != null && s.Unique) ? s.ResolvedRva : -1;
        }

        /// <summary>解析内存 PE 头，把 .text 节整段读进托管数组。</summary>
        public bool MapText()
        {
            try
            {
                int lfanew = Marshal.ReadInt32(_base, 0x3C);
                if (lfanew <= 0 || lfanew > 0x1000) return false;

                short nsec = Marshal.ReadInt16(_base, lfanew + 6);
                short optSize = Marshal.ReadInt16(_base, lfanew + 20);
                if (nsec <= 0 || nsec > 96) return false;

                IntPtr sec0 = _base + lfanew + 24 + optSize;
                for (int i = 0; i < nsec; i++)
                {
                    IntPtr sh = sec0 + i * 40;
                    byte[] nm = new byte[8];
                    Marshal.Copy(sh, nm, 0, 8);
                    string name = Encoding.ASCII.GetString(nm).TrimEnd('\0');
                    if (name != ".text") continue;

                    int vsize = Marshal.ReadInt32(sh, 8);
                    int va = Marshal.ReadInt32(sh, 12);
                    int rawSize = Marshal.ReadInt32(sh, 16);
                    int size = vsize > 0 ? vsize : rawSize;
                    if (size <= 0 || size > 0x8000000 || va <= 0) return false;

                    byte[] buf = new byte[size];
                    Marshal.Copy(_base + va, buf, 0, size);
                    _text = buf;
                    _textRva = va;
                    _textLen = size;
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        public void ScanAll()
        {
            foreach (SigResult s in _sigs) ScanOne(s);
        }

        private void ScanOne(SigResult s)
        {
            s.Scanned = true;
            s.Hits = 0;
            s.HitsCapped = false;
            s.FirstHitRva = -1;
            s.ResolvedRva = -1;

            if (_text == null) { s.Reason = ".text 不可用"; return; }

            int n = s.Pat.Length;
            int limit = _textLen - n;
            if (limit < 0) { s.Reason = "签名长度超过 .text"; return; }

            byte first = s.Pat[0];
            int hits = 0, firstHit = -1, i = 0;

            while (true)
            {
                i = Array.IndexOf(_text, first, i);
                if (i < 0 || i > limit) break;

                if (MatchAt(i, s.Pat, s.Mask))
                {
                    hits++;
                    if (firstHit < 0) firstHit = i;
                    if (hits >= MaxHits) { s.HitsCapped = true; break; }
                }
                i++;
            }

            s.Hits = hits;
            if (hits > 0) s.FirstHitRva = _textRva + firstHit;

            if (hits == 0) s.Reason = "0 处命中 → 签名不可用";
            else if (hits == 1) { s.ResolvedRva = s.FirstHitRva; s.Reason = "唯一命中"; }
            else s.Reason = (s.HitsCapped ? ">=" : "") + hits + " 处命中 → 不唯一，签名不可用";
        }

        private bool MatchAt(int off, byte[] pat, byte[] mask)
        {
            for (int k = 0; k < pat.Length; k++)
            {
                if (mask[k] != 0 && _text[off + k] != pat[k]) return false;
            }
            return true;
        }

        public string Describe(SigResult s)
        {
            if (s == null) return "(缺失)";
            if (!s.Unique) return s.Reason;
            int delta = s.ResolvedRva - s.BaselineRva;
            return delta == 0 ? "唯一命中，RVA 差值 0" : ("唯一命中，RVA 差值 " + Signed(delta));
        }

        public static string Signed(int v)
        {
            return (v >= 0 ? "+0x" : "-0x") + Math.Abs(v).ToString("X");
        }
    }
}
