// ============================================================================
//  VMRecoilAuto — 自动定位层（长期做法核心）
// ----------------------------------------------------------------------------
//  思路：不猜地址，靠"这个常量长什么样"去认它。
//
//    第一步 值指纹：把一个 float（默认 0.325f）的 4 字节 bit 模式在整个模块镜像
//                    的数据节里搜。原版这两个数值在 client.dll 里只出现一次，
//                    这种"唯一性"就是天然的指纹。
//
//    第二步 引用反查：扫 .text，找所有 RIP 相对寻址的 movss（F3 0F 10 / F3 0F 11，
//                    modrm.mod=00 且 rm=101），算出各自指向的地址。恰好有 1 条指令
//                    指向第一步那个常量 → 说明它就是"被代码真正使用的那一份"。
//                    这一步是为了排除"数据节里有同一数值的冷数据副本"。
//
//    第三步 安全闸门：值必须对、常量必须唯一、引用必须唯一。任一不满足 → 返回失败，
//                    调用方空转，一个字节都不写。
//
//  两版实测（1.40.4.5 与 1.41.9.x）：常量的 RVA 从 0x12E11BC 变成 0x1D12A38，
//  但"0.325f 在数据节唯一"+"指向它的 movss 唯一"这两条在两边都成立。
//  所以这套定位不依赖版本，只依赖"引擎仍在用 0.325 这个数值"。
//
//  另外：cvar 注册三件套（refinit / regcore / getfloat）没有可反推的数值特征，
//  只能靠字节签名 —— 这部分被做成外部配置（见 AutoConfig.cs），失效时改配置即可。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace VMRecoilAuto
{
    internal sealed class PeSection
    {
        public string Name = "";
        public int Va;
        public int Size;
        public bool Readable;
        public bool Writable;
        public bool Executable;

        public string Kind { get { return Executable ? "代码" : (Writable ? "可写数据" : "只读数据"); } }
    }

    internal sealed class ConstantHit
    {
        public int Rva;
        public int Refs;
        public int FirstRefRva = -1;
        public bool Exact;
    }

    internal sealed class SigHit
    {
        public string Name = "";
        public int Hits;
        public int HitsCapped;
        public int FirstHitRva = -1;
        public string Reason = "未扫描";
        public int BaselineRva = -1;

        public bool Unique { get { return Hits == 1 && FirstHitRva > 0; } }
    }

    internal sealed class AutoLocator
    {
        private const int MaxSigHits = 16;
        private const int MaxConstantCandidates = 64;
        private const int ChunkSize = 4 * 1024 * 1024;

        private readonly IntPtr _base;
        private readonly int _imageSize;
        private readonly List<PeSection> _sections = new List<PeSection>();

        private byte[] _text;
        private int _textVa;

        public readonly List<string> Notes = new List<string>();

        public bool Mapped { get { return _text != null; } }
        public IList<PeSection> Sections { get { return _sections; } }

        public AutoLocator(IntPtr baseAddr, int imageSize)
        {
            _base = baseAddr;
            _imageSize = imageSize;
        }

        // ---------------------------------------------------------------- PE 解析
        /// <summary>枚举节表；顺带说明每个节是否参与常量搜索。</summary>
        public bool MapSections()
        {
            try
            {
                int lfanew = Marshal.ReadInt32(_base, 0x3C);
                if (lfanew <= 0 || lfanew > 0x1000) { Notes.Add("PE 头 lfanew 异常：" + lfanew); return false; }

                short nsec = Marshal.ReadInt16(_base, lfanew + 6);
                short optSize = Marshal.ReadInt16(_base, lfanew + 20);
                if (nsec <= 0 || nsec > 96) { Notes.Add("PE 节数量异常：" + nsec); return false; }

                IntPtr sec0 = _base + lfanew + 24 + optSize;
                for (int i = 0; i < nsec; i++)
                {
                    IntPtr sh = sec0 + i * 40;
                    byte[] nm = new byte[8];
                    Marshal.Copy(sh, nm, 0, 8);
                    string name = Encoding.ASCII.GetString(nm).TrimEnd('\0');

                    int vsize = Marshal.ReadInt32(sh, 8);
                    int va = Marshal.ReadInt32(sh, 12);
                    int rawSize = Marshal.ReadInt32(sh, 16);
                    int chars = Marshal.ReadInt32(sh, 36);

                    int size = vsize > 0 ? vsize : rawSize;
                    if (size <= 0 || va <= 0) continue;
                    if (va + size > _imageSize) size = _imageSize - va;
                    if (size <= 0) continue;

                    PeSection s = new PeSection();
                    s.Name = name;
                    s.Va = va;
                    s.Size = size;
                    s.Readable = (chars & 0x40000000) != 0;
                    s.Writable = (chars & 0x80000000) != 0;
                    s.Executable = (chars & 0x20000000) != 0;
                    _sections.Add(s);
                }

                if (_sections.Count == 0) { Notes.Add("没有解析出任何可用节"); return false; }
                return true;
            }
            catch (Exception ex)
            {
                Notes.Add("解析 PE 节表失败：" + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>把 .text 整段读进托管内存（签名扫描与引用反查都要用）。</summary>
        public bool MapText()
        {
            foreach (PeSection s in _sections)
            {
                if (s.Name != ".text" || !s.Readable) continue;
                if (s.Size > 0x8000000) { Notes.Add(".text 过大（" + Fmt.Size(s.Size) + "），放弃映射"); return false; }
                try
                {
                    byte[] buf = new byte[s.Size];
                    Marshal.Copy(_base + s.Va, buf, 0, s.Size);
                    _text = buf;
                    _textVa = s.Va;
                    return true;
                }
                catch (Exception ex)
                {
                    Notes.Add("读取 .text 失败：" + ex.GetType().Name + ": " + ex.Message);
                    return false;
                }
            }
            Notes.Add("找不到可读的 .text 节");
            return false;
        }

        // ---------------------------------------------------------------- 第一步：值指纹
        /// <summary>
        /// 在整个镜像里找 float 的 bit 模式。先精确匹配，精确 0 命中再按容差兜底。
        /// 返回所有候选 RVA（上限 MaxConstantCandidates，超出视为"太常见、不可靠"）。
        /// </summary>
        public List<ConstantHit> FindConstant(float value, float tolerance)
        {
            List<ConstantHit> exact = ScanAll(value, -1f);
            if (exact.Count > 0)
            {
                foreach (ConstantHit h in exact) h.Exact = true;
                return exact;
            }

            if (tolerance > 0f)
            {
                List<ConstantHit> loose = ScanAll(value, tolerance);
                foreach (ConstantHit h in loose) h.Exact = false;
                return loose;
            }

            return exact;
        }

        private List<ConstantHit> ScanAll(float value, float tolerance)
        {
            List<ConstantHit> result = new List<ConstantHit>();
            foreach (PeSection s in _sections)
            {
                if (!s.Readable) continue;
                ScanSection(s, value, tolerance, result);
                if (result.Count > MaxConstantCandidates) break;
            }
            return result;
        }

        private void ScanSection(PeSection sec, float value, float tolerance, List<ConstantHit> outList)
        {
            byte[] bits = BitConverter.GetBytes(value);
            bool exact = tolerance < 0f;

            byte[] buf = new byte[ChunkSize];
            int pos = 0;

            while (pos < sec.Size)
            {
                int want = Math.Min(ChunkSize, sec.Size - pos);
                try
                {
                    Marshal.Copy(_base + sec.Va + pos, buf, 0, want);
                }
                catch (Exception ex)
                {
                    Notes.Add("读取节 " + sec.Name + " 失败：" + ex.GetType().Name + ": " + ex.Message);
                    return;
                }

                for (int i = 0; i + 4 <= want; i++)
                {
                    if (exact)
                    {
                        if (buf[i] != bits[0] || buf[i + 1] != bits[1] || buf[i + 2] != bits[2] || buf[i + 3] != bits[3]) continue;
                    }
                    else
                    {
                        float v = BitConverter.ToSingle(buf, i);
                        if (float.IsNaN(v) || Math.Abs(v - value) > tolerance) continue;
                    }

                    ConstantHit h = new ConstantHit();
                    h.Rva = sec.Va + pos + i;
                    outList.Add(h);
                    if (outList.Count > MaxConstantCandidates) return;
                }

                if (want < ChunkSize) break;
                pos += want - 3;   // 保留 3 字节重叠，避免常量跨块被漏掉
            }
        }

        // ---------------------------------------------------------------- 第二步：引用反查
        /// <summary>
        /// 统计 .text 中指向 constRva 的 RIP 相对 movss 指令条数。
        /// 覆盖两种编码：F3 0F 10 /r（读）与 F3 0F 11 /r（写），仅 mod=00 rm=101（RIP+disp32）。
        /// </summary>
        public int CountRefs(int constRva, out int firstRefRva)
        {
            firstRefRva = -1;
            int count = 0;
            if (_text == null) return 0;

            int limit = _text.Length - 8;
            for (int i = 0; i <= limit; i++)
            {
                if (_text[i] != 0xF3) continue;
                if (_text[i + 1] != 0x0F) continue;
                byte op = _text[i + 2];
                if (op != 0x10 && op != 0x11) continue;

                byte modrm = _text[i + 3];
                if ((modrm & 0xC7) != 0x05) continue;   // mod=00, rm=101 → RIP 相对

                int disp = BitConverter.ToInt32(_text, i + 4);
                long target = (long)_textVa + i + 8 + disp;
                if (target != constRva) continue;

                count++;
                if (firstRefRva < 0) firstRefRva = _textVa + i;
                if (count > MaxConstantCandidates) break;
            }
            return count;
        }

        // ---------------------------------------------------------------- 第三步：签名扫描（cvar 三件套）
        public SigHit ScanSig(string name, string pattern, int baselineRva)
        {
            SigHit hit = new SigHit();
            hit.Name = name;
            hit.BaselineRva = baselineRva;

            byte[] pat, mask;
            if (!Compile(pattern, out pat, out mask))
            {
                hit.Reason = "签名字符串格式错误（应为空格分隔的 hex，通配写 ??）";
                return hit;
            }

            if (_text == null) { hit.Reason = ".text 不可用"; return hit; }

            int n = pat.Length;
            if (n < 4 || n > _text.Length) { hit.Reason = "签名长度不合理"; return hit; }

            int first = -1, hits = 0, capped = 0;
            int i0 = 0;
            while (true)
            {
                i0 = Array.IndexOf(_text, pat[0], i0);
                if (i0 < 0 || i0 + n > _text.Length) break;

                bool ok = true;
                for (int k = 0; k < n; k++)
                {
                    if (mask[k] != 0 && _text[i0 + k] != pat[k]) { ok = false; break; }
                }

                if (ok)
                {
                    hits++;
                    if (first < 0) first = i0;
                    if (hits >= MaxSigHits) { capped = 1; break; }
                }
                i0++;
            }

            hit.Hits = hits;
            hit.HitsCapped = capped;
            if (hits > 0) hit.FirstHitRva = _textVa + first;

            if (hits == 0) hit.Reason = "0 处命中 → 该签名在这一版 client.dll 里已失效";
            else if (hits == 1) hit.Reason = "唯一命中" + DeltaText(hit.FirstHitRva, baselineRva);
            else hit.Reason = (capped == 1 ? ">=" : "") + hits + " 处命中 → 不唯一，拒用";

            return hit;
        }

        private static string DeltaText(int rva, int baseline)
        {
            if (baseline <= 0) return "（未设置 baselineRva）";
            int d = rva - baseline;
            if (d == 0) return "，RVA 差值 0";
            return "，RVA 差值 " + (d > 0 ? "+0x" : "-0x") + Math.Abs(d).ToString("X");
        }

        private static bool Compile(string pattern, out byte[] pat, out byte[] mask)
        {
            pat = null;
            mask = null;
            if (string.IsNullOrWhiteSpace(pattern)) return false;

            string[] parts = pattern.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;

            pat = new byte[parts.Length];
            mask = new byte[parts.Length];

            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p == "?" || p == "??") { pat[i] = 0; mask[i] = 0; continue; }
                if (p.Length != 2) return false;
                try
                {
                    pat[i] = Convert.ToByte(p, 16);
                    mask[i] = 1;
                }
                catch { return false; }
            }
            return true;
        }

        // ---------------------------------------------------------------- 报告
        public string DescribeSections()
        {
            StringBuilder sb = new StringBuilder();
            foreach (PeSection s in _sections)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0}({1},{2})", s.Name, s.Kind, Fmt.Size(s.Size));
            }
            return sb.ToString();
        }

        public static class Fmt
        {
            public static string Size(int bytes)
            {
                if (bytes >= 1024 * 1024) return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + "MB";
                if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + "KB";
                return bytes + "B";
            }
        }
    }
}
