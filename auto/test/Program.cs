// ============================================================================
//  LocatorTest — 离线验证 AutoLocator（不开游戏也能自检）
// ----------------------------------------------------------------------------
//  做法：把磁盘上的 client.dll 按 PE 内存布局展开成一份镜像（不是直接读文件，
//        因为文件是 FileAlignment 对齐、内存是 SectionAlignment 对齐，RVA 不一致），
//        固定到内存后用与插件完全相同的 AutoLocator 跑一遍定位。
//
//  验证项：
//    1) 0.325f 值指纹：在镜像里命中几个、RVA 是多少
//    2) 引用反查：指向该常量的 movss 有几条、指令在哪
//    3) 最终判定：是否唯一、RVA 是否等于实机已知值
//    4) cvar 三件套签名：各命中几处（1 处才算可用）
//
//  用法：LocatorTest.exe <client.dll 路径> [期望RVA(hex)]
// ============================================================================

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using VMRecoilAuto;

namespace LocatorTest
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("用法: LocatorTest <client.dll> [期望RVA(hex)]");
                return 2;
            }

            string dllPath = args[0];
            int expectRva = -1;
            if (args.Length >= 2) expectRva = ParseRva(args[1]);

            if (!File.Exists(dllPath))
            {
                Console.WriteLine("文件不存在: " + dllPath);
                return 2;
            }

            Console.WriteLine("================================================================");
            Console.WriteLine("文件: " + dllPath);
            Console.WriteLine("大小: " + new FileInfo(dllPath).Length.ToString("N0", CultureInfo.InvariantCulture) + " 字节");

            byte[] file = File.ReadAllBytes(dllPath);
            byte[] image;
            int sizeOfImage;
            if (!BuildImage(file, out image, out sizeOfImage))
            {
                Console.WriteLine("镜像展开失败");
                return 1;
            }
            Console.WriteLine("镜像: SizeOfImage = 0x" + sizeOfImage.ToString("X") + " (" + AutoLocator.Fmt.Size(sizeOfImage) + ")");

            GCHandle pin = GCHandle.Alloc(image, GCHandleType.Pinned);
            try
            {
                IntPtr baseAddr = pin.AddrOfPinnedObject();

                AutoLocator loc = new AutoLocator(baseAddr, sizeOfImage);
                if (!loc.MapSections())
                {
                    Dump(loc);
                    return 1;
                }
                Console.WriteLine("节表: " + loc.DescribeSections());

                bool ok = true;

                // ---------------------------------------------------------- 1. 值指纹
                Console.WriteLine();
                Console.WriteLine("---- [1] 值指纹 0.325f " + string.Concat("--------"));
                var cands = loc.FindConstant(0.325f, 1e-4f);
                Console.WriteLine("候选数: " + cands.Count);
                int shown = 0;
                foreach (var h in cands)
                {
                    if (shown++ >= 20) { Console.WriteLine("  ..."); break; }
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  RVA 0x{0:X}  值={1:F7}  {2}", h.Rva, ReadF(baseAddr, h.Rva), h.Exact ? "精确" : "容差"));
                }

                if (cands.Count == 0) { Console.WriteLine("结果: 未命中"); ok = false; }

                // ---------------------------------------------------------- 2. 引用反查
                Console.WriteLine();
                Console.WriteLine("---- [2] 引用反查 ----");
                if (!loc.MapText()) { Dump(loc); return 1; }

                foreach (var h in cands)
                {
                    int firstRef;
                    int refs = loc.CountRefs(h.Rva, out firstRef);
                    h.Refs = refs;
                    h.FirstRefRva = firstRef;
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  RVA 0x{0:X} → movss 引用 {1} 条{2}", h.Rva, refs,
                        firstRef > 0 ? string.Format(CultureInfo.InvariantCulture, "（指令 RVA 0x{0:X}）", firstRef) : ""));
                }

                // ---------------------------------------------------------- 3. 最终判定
                Console.WriteLine();
                Console.WriteLine("---- [3] 最终判定 ----");
                ConstantHit pick = null;
                int uniqueRef = 0;
                foreach (var h in cands) if (h.Refs == 1) { uniqueRef++; pick = h; }

                if (cands.Count == 1 && uniqueRef == 1)
                {
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  采信: RVA 0x{0:X}（唯一常量 + 唯一 movss 引用）", pick.Rva));
                    if (expectRva > 0)
                    {
                        bool match = pick.Rva == expectRva;
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "  期望: 0x{0:X} → {1}", expectRva, match ? "一致 [PASS]" : "不一致 [FAIL]"));
                        ok = ok && match;
                    }
                }
                else
                {
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  放弃: 候选 {0} 个，唯一引用 {1} 个 → 安全闸门拒绝", cands.Count, uniqueRef));
                    ok = false;
                }

                // ---------------------------------------------------------- 4. 签名
                Console.WriteLine();
                Console.WriteLine("---- [4] cvar 三件套签名 ----");
                AutoConfig cfg = AutoConfig.Default();
                foreach (var kv in cfg.Signatures)
                {
                    SigHit sh = loc.ScanSig(kv.Key, kv.Value.Pattern, kv.Value.BaselineRva);
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  {0,-9} 命中 {1,2} 处  {2}  {3}", kv.Key, sh.Hits, sh.Reason,
                        sh.Unique ? string.Format(CultureInfo.InvariantCulture, "RVA=0x{0:X}", sh.FirstHitRva) : ""));
                }

                Dump(loc);

                Console.WriteLine();
                Console.WriteLine("结论: " + (ok ? "PASS" : "FAIL"));
                return ok ? 0 : 1;
            }
            finally
            {
                pin.Free();
            }
        }

        private static void Dump(AutoLocator loc)
        {
            foreach (string n in loc.Notes) Console.WriteLine("  [note] " + n);
        }

        private static float ReadF(IntPtr baseAddr, int rva)
        {
            try { return BitConverter.Int32BitsToSingle(Marshal.ReadInt32(baseAddr, rva)); }
            catch { return float.NaN; }
        }

        private static int ParseRva(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                int v;
                if (int.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return v;
                return -1;
            }
            int d;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out d)) return d;
            return -1;
        }

        /// <summary>按 PE 内存布局把文件展开成镜像（节按 VirtualAddress 摆放）。</summary>
        private static bool BuildImage(byte[] file, out byte[] image, out int sizeOfImage)
        {
            image = null;
            sizeOfImage = 0;
            try
            {
                if (file.Length < 0x200) return false;

                int lfanew = BitConverter.ToInt32(file, 0x3C);
                if (lfanew <= 0 || lfanew + 0x100 > file.Length) return false;

                short nsec = BitConverter.ToInt16(file, lfanew + 6);
                short optSize = BitConverter.ToInt16(file, lfanew + 20);
                if (nsec <= 0 || nsec > 96) return false;

                int optOff = lfanew + 24;
                sizeOfImage = BitConverter.ToInt32(file, optOff + 56);
                if (sizeOfImage <= 0 || sizeOfImage > 0x20000000) return false;

                image = new byte[sizeOfImage];

                // PE 头（含节表）不属于任何节，必须单独拷：内存布局里它占据 0 .. SizeOfHeaders
                int sizeOfHeaders = BitConverter.ToInt32(file, optOff + 60);
                if (sizeOfHeaders > 0 && sizeOfHeaders <= file.Length && sizeOfHeaders <= sizeOfImage)
                    Array.Copy(file, 0, image, 0, sizeOfHeaders);

                int sec0 = optOff + optSize;
                for (int i = 0; i < nsec; i++)
                {
                    int sh = sec0 + i * 40;
                    int vsize = BitConverter.ToInt32(file, sh + 8);
                    int va = BitConverter.ToInt32(file, sh + 12);
                    int rawSize = BitConverter.ToInt32(file, sh + 16);
                    int rawPtr = BitConverter.ToInt32(file, sh + 20);

                    if (va <= 0 || va >= sizeOfImage) continue;
                    int n = Math.Min(rawSize, sizeOfImage - va);
                    if (n <= 0) continue;
                    if (rawPtr < 0 || rawPtr + n > file.Length) n = file.Length - rawPtr;
                    if (n <= 0) continue;

                    Array.Copy(file, rawPtr, image, va, n);
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("BuildImage 异常: " + ex.Message);
                return false;
            }
        }
    }
}
