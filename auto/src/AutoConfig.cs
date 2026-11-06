// ============================================================================
//  VMRecoilAuto — 外部配置层
// ----------------------------------------------------------------------------
//  目的：把"每一版 client.dll 都会变的东西"从代码里挪到进程外的 JSON 里。
//        代码只保留算法（值指纹 + 引用反查），不保留任何版本的硬地址。
//
//  配置文件：<插件目录>\vmrecoil_auto.json
//    - 不存在 → 首次运行自动生成一份带注释的默认配置（内容等于当前已知可用值）
//    - 存在   → 解析；解析失败的字段保留默认值并写日志，不因此中断插件
//    - 支持 // 注释与尾随逗号（方便用户直接在文件里写说明）
//
//  设计原则：配置是"提示"，不是"授权"。
//    无论配置里写了什么，定位结果都必须通过 AutoLocator 的安全校验才会被真正写入。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VMRecoilAuto
{
    /// <summary>单条 cvar 注册链签名（pattern 支持 ?? 通配）。</summary>
    internal sealed class CvarSig
    {
        public string Pattern;
        public int BaselineRva = -1;   // 仅用于日志里显示"RVA 差值"，-1 表示不关心
        public bool Optional;          // true = 找不到/不唯一时只降级，不阻断（getfloat 就是这种）

        public CvarSig(string pattern, int baselineRva, bool optional = false)
        {
            Pattern = pattern;
            BaselineRva = baselineRva;
            Optional = optional;
        }
    }

    internal sealed class AutoConfig
    {
        public const string FileName = "vmrecoil_auto.json";

        // ---- 行为参数
        public string CvarName = "viewmodel_recoil";
        public float OnValue = 0.325f;          // 原版常量（"开"时写入）
        public float OffValue = 0.0f;           // "关"时写入
        public float ValueTolerance = 1e-4f;    // 常量值比对容差
        public bool RequireUniqueConstant = true;   // 常量在整个 image 必须唯一命中
        public bool RequireUniqueReference = true;  // 指向常量的 movss 必须唯一

        // ---- 可选加速提示（不是必需，留空则完全自动）
        public readonly List<int> RvaHints = new List<int>();

        // ---- cvar 注册三件套签名（跨版本最易变，故外部化）
        public readonly Dictionary<string, CvarSig> Signatures =
            new Dictionary<string, CvarSig>(StringComparer.OrdinalIgnoreCase);

        // ---- 解析过程中的提示（供日志输出）
        public readonly List<string> Notes = new List<string>();

        public static AutoConfig Default()
        {
            AutoConfig c = new AutoConfig();
            c.Signatures["refinit"] = new CvarSig(
                "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 B8 FF FF FF FF 49 0F", 0x110EDB0);
            c.Signatures["regcore"] = new CvarSig(
                "48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 55 41 54 41 55 41 56 41 57 48 8D 6C 24 D1", 0x110FAE0);
            c.Signatures["getfloat"] = new CvarSig(
                "40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 48 85 C0 74 0A F3 0F 10 00 48 83 C4 20 5B C3", 0x110DA0, true);
            return c;
        }

        /// <summary>
        /// 读配置；文件不存在则写一份默认配置。任何解析异常都不会抛给调用方。
        /// </summary>
        public static AutoConfig LoadOrCreate(string path)
        {
            AutoConfig cfg = Default();

            try
            {
                if (!File.Exists(path))
                {
                    try
                    {
                        File.WriteAllText(path, BuildDefaultText(), new UTF8Encoding(false));
                        cfg.Notes.Add("配置文件不存在，已生成默认配置：" + path);
                    }
                    catch (Exception ex)
                    {
                        cfg.Notes.Add("配置文件不存在且写入失败（将使用内置默认值）：" + ex.GetType().Name + ": " + ex.Message);
                    }
                    return cfg;
                }

                string json = File.ReadAllText(path, Encoding.UTF8);
                ParseInto(json, cfg);
                cfg.Notes.Add("已读取外部配置：" + path);
            }
            catch (Exception ex)
            {
                cfg.Notes.Add("读取外置配置失败，回退到内置默认值：" + ex.GetType().Name + ": " + ex.Message);
            }

            return cfg;
        }

        private static void ParseInto(string json, AutoConfig cfg)
        {
            JsonDocumentOptions opt = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            using (JsonDocument doc = JsonDocument.Parse(json, opt))
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    cfg.Notes.Add("配置根节点不是对象，整份配置按默认值处理");
                    return;
                }

                cfg.CvarName = Str(root, "cvarName", cfg.CvarName, cfg, 64);
                cfg.OnValue = Num(root, "onValue", cfg.OnValue, cfg);
                cfg.OffValue = Num(root, "offValue", cfg.OffValue, cfg);

                if (root.TryGetProperty("locate", out JsonElement loc) && loc.ValueKind == JsonValueKind.Object)
                {
                    cfg.ValueTolerance = Num(loc, "valueTolerance", cfg.ValueTolerance, cfg);
                    cfg.RequireUniqueConstant = Bool(loc, "requireUniqueConstant", cfg.RequireUniqueConstant, cfg);
                    cfg.RequireUniqueReference = Bool(loc, "requireUniqueReference", cfg.RequireUniqueReference, cfg);

                    if (loc.TryGetProperty("rvaHints", out JsonElement hints) && hints.ValueKind == JsonValueKind.Array)
                    {
                        cfg.RvaHints.Clear();
                        foreach (JsonElement h in hints.EnumerateArray())
                        {
                            int rva = Rva(h);
                            if (rva > 0) cfg.RvaHints.Add(rva);
                            else cfg.Notes.Add("locate.rvaHints 中有无法解析的项，已忽略：" + h.ToString());
                        }
                    }
                }

                if (root.TryGetProperty("cvarSignatures", out JsonElement sigs) && sigs.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in sigs.EnumerateObject())
                    {
                        if (p.Value.ValueKind != JsonValueKind.Object)
                        {
                            cfg.Notes.Add("cvarSignatures." + p.Name + " 不是对象，已忽略该项");
                            continue;
                        }

                        string pat = Str(p.Value, "pattern", null, cfg, 512);
                        if (string.IsNullOrWhiteSpace(pat))
                        {
                            cfg.Notes.Add("cvarSignatures." + p.Name + ".pattern 为空，已保留内置默认签名");
                            continue;
                        }

                        int baseline = -1;
                        if (p.Value.TryGetProperty("baselineRva", out JsonElement br))
                        {
                            baseline = Rva(br);
                            if (baseline <= 0) cfg.Notes.Add("cvarSignatures." + p.Name + ".baselineRva 无法解析（仅影响日志显示）");
                        }

                        // optional 未写时沿用内置默认（getfloat 默认就是 optional，不许被静默改回必需）
                        bool optional = false;
                        if (cfg.Signatures.TryGetValue(p.Name, out CvarSig prev) && prev != null) optional = prev.Optional;
                        optional = Bool(p.Value, "optional", optional, cfg);

                        cfg.Signatures[p.Name] = new CvarSig(pat.Trim(), baseline, optional);
                    }
                }
            }

            // 值域校验：任一不合法都退回默认，避免用户手改出诡异值的静默生效
            if (!(cfg.OnValue > 0f) || float.IsNaN(cfg.OnValue) || float.IsInfinity(cfg.OnValue))
            {
                cfg.Notes.Add("onValue 非法（必须 > 0），已回退 0.325");
                cfg.OnValue = 0.325f;
            }
            if (float.IsNaN(cfg.OffValue) || float.IsInfinity(cfg.OffValue))
            {
                cfg.Notes.Add("offValue 非法，已回退 0");
                cfg.OffValue = 0f;
            }
            if (!(cfg.ValueTolerance > 0f) || cfg.ValueTolerance > 0.01f)
            {
                cfg.Notes.Add("valueTolerance 超出 (0, 0.01]，已回退 1e-4");
                cfg.ValueTolerance = 1e-4f;
            }
            if (string.IsNullOrWhiteSpace(cfg.CvarName) || cfg.CvarName.IndexOf(' ') >= 0)
            {
                cfg.Notes.Add("cvarName 非法，已回退 viewmodel_recoil");
                cfg.CvarName = "viewmodel_recoil";
            }
        }

        private static string Str(JsonElement obj, string name, string dflt, AutoConfig cfg, int maxLen)
        {
            if (!obj.TryGetProperty(name, out JsonElement e)) return dflt;
            if (e.ValueKind != JsonValueKind.String)
            {
                cfg.Notes.Add(name + " 不是字符串，已忽略");
                return dflt;
            }
            string s = e.GetString();
            if (s == null) return dflt;
            if (s.Length > maxLen) s = s.Substring(0, maxLen);
            return s;
        }

        private static float Num(JsonElement obj, string name, float dflt, AutoConfig cfg)
        {
            if (!obj.TryGetProperty(name, out JsonElement e)) return dflt;
            if (e.ValueKind != JsonValueKind.Number)
            {
                cfg.Notes.Add(name + " 不是数字，已忽略");
                return dflt;
            }
            if (e.TryGetDouble(out double d)) return (float)d;
            cfg.Notes.Add(name + " 无法转为数字，已忽略");
            return dflt;
        }

        private static bool Bool(JsonElement obj, string name, bool dflt, AutoConfig cfg)
        {
            if (!obj.TryGetProperty(name, out JsonElement e)) return dflt;
            if (e.ValueKind == JsonValueKind.True) return true;
            if (e.ValueKind == JsonValueKind.False) return false;
            cfg.Notes.Add(name + " 不是布尔值，已忽略");
            return dflt;
        }

        /// <summary>RVA 允许写 "0x12E11BC"（字符串）或 19861948（数字）。</summary>
        private static int Rva(JsonElement e)
        {
            try
            {
                if (e.ValueKind == JsonValueKind.Number)
                {
                    if (e.TryGetInt64(out long l) && l > 0 && l <= int.MaxValue) return (int)l;
                    return -1;
                }
                if (e.ValueKind == JsonValueKind.String)
                {
                    string s = e.GetString();
                    if (string.IsNullOrWhiteSpace(s)) return -1;
                    s = s.Trim();
                    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex) && hex > 0)
                            return hex;
                        return -1;
                    }
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dec) && dec > 0)
                        return dec;
                }
            }
            catch { }
            return -1;
        }

        /// <summary>配置自述（打进日志，让人一眼看到"这次到底按什么参数在跑"）。</summary>
        public string Summary()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "cvar={0} on={1} off={2} tol={3:0.####} 唯一常量={4} 唯一引用={5} rvaHints={6} 外部签名={7}",
                CvarName, OnValue, OffValue, ValueTolerance,
                RequireUniqueConstant ? "是" : "否", RequireUniqueReference ? "是" : "否",
                RvaHints.Count == 0 ? "无" : RvaHints.Count + " 条",
                string.Join("/", new List<string>(Signatures.Keys).ToArray()));
            return sb.ToString();
        }

        /// <summary>首次运行生成的默认配置文件正文（带说明性注释）。</summary>
        private static string BuildDefaultText()
        {
            AutoConfig d = Default();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("// ===========================================================================");
            sb.AppendLine("//  VMRecoilAuto 外部配置   （首次运行自动生成；改完保存后重载插件即生效）");
            sb.AppendLine("// ---------------------------------------------------------------------------");
            sb.AppendLine("//  为什么有这个文件：");
            sb.AppendLine("//    client.dll 每次更新，函数位置都可能变。本插件不把任何版本地址写死在代码里，");
            sb.AppendLine("//    常量靠值指纹 + 引用反查自动定位；只有三件套 cvar 函数签名无法自动推导，");
            sb.AppendLine("//    所以放在这里 —— 需要时改这里，不用重新编译，也不用找我。");
            sb.AppendLine("//");
            sb.AppendLine("//  什么时候需要改：");
            sb.AppendLine("//    1) 插件日志出现「cvar 注册链签名不可用」→ 说明这三条签名在某版失效了；");
            sb.AppendLine("//       把新签名填进 cvarSignatures（格式：字节 hex，用空格分隔，?? 表示通配）；");
            sb.AppendLine("//    2) 日志出现「常量指纹不唯一 / 无命中」→ 先确认 onValue 是否仍等于原版常量；");
            sb.AppendLine("//       Valve 一旦改了 0.325 这个数值，只要把 onValue 改成新值即可继续工作；");
            sb.AppendLine("//    3) 想看加速：把已知地址写进 locate.rvaHints（可选，只用于优先尝试，不写也行）。");
            sb.AppendLine("//");
            sb.AppendLine("//  安全兜底（写死不可关）：");
            sb.AppendLine("//    常量值必须落在 onValue±valueTolerance；常量命中必须唯一；指向它的 movss 必须唯一。");
            sb.AppendLine("//    任一条不满足，插件空转，一个字节都不写。");
            sb.AppendLine("// ===========================================================================");
            sb.AppendLine("{");
            sb.AppendLine("  // ---- cvar 名称（控制台里敲的命令）");
            sb.AppendLine("  \"cvarName\": \"" + d.CvarName + "\",");
            sb.AppendLine();
            sb.AppendLine("  // ---- 原版常量值：必须与 client.dll 里那个 float 完全一致");
            sb.AppendLine("  \"onValue\": 0.325,");
            sb.AppendLine("  // ---- 关闭时写入的值");
            sb.AppendLine("  \"offValue\": 0.0,");
            sb.AppendLine();
            sb.AppendLine("  \"locate\": {");
            sb.AppendLine("    // 常量值比对容差");
            sb.AppendLine("    \"valueTolerance\": 0.0001,");
            sb.AppendLine("    // 常量在整个镜像（所有节）里必须只出现一次");
            sb.AppendLine("    \"requireUniqueConstant\": true,");
            sb.AppendLine("    // 指向该常量的 movss 必须只出现一次");
            sb.AppendLine("    \"requireUniqueReference\": true,");
            sb.AppendLine("    // 可选：已知地址优先尝试，例如 [\"0x12E11BC\"]；留空则完全自动");
            sb.AppendLine("    \"rvaHints\": []");
            sb.AppendLine("  },");
            sb.AppendLine();
            sb.AppendLine("  // ---- 引擎 cvar 注册三件套签名（跨版本最容易变的就是这三条）");
            sb.AppendLine("  \"cvarSignatures\": {");
            bool first = true;
            foreach (KeyValuePair<string, CvarSig> kv in d.Signatures)
            {
                if (!first) sb.AppendLine(",");
                first = false;
                sb.Append("    \"" + kv.Key + "\": { \"pattern\": \"" + kv.Value.Pattern + "\", \"baselineRva\": \"0x" +
                          kv.Value.BaselineRva.ToString("X") + "\"" +
                          (kv.Value.Optional ? ", \"optional\": true" : "") + " }");
            }
            sb.AppendLine();
            sb.AppendLine("  }");
            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
