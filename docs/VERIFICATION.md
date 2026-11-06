# 定位算法验证报告（离线复现）

本文件说明如何**不装游戏、不开游戏**，只拿一个 `client.dll` 就验证 `VMRecoilAuto` 的定位算法是否成立。
所有数字来源可自行复现。

---

## 1. 为什么需要这个工具

`VMRecoilAuto` 的核心主张是："不靠硬编码地址，靠值指纹 + 引用反查自动找到 `0.325f` 常量。"

这个主张必须能被独立验证，否则读者只能选择相信作者。
所以 `auto/test/` 里放了一个 `LocatorTest`：它把真实 `client.dll` 按 PE 内存布局展开成镜像，
然后**跑的是和插件完全相同的定位代码**（同一个 `AutoLocator.cs`），把每一步中间结果打印出来。

---

## 2. 用法

```bat
cd auto\test
dotnet build LocatorTest.csproj -c Release

:: 参数1 = client.dll 路径
:: 参数2 = 你已知的常量 RVA（可选，填了会做一致性比对；不知道就写 0）
dotnet bin\Release\net8.0\LocatorTest.dll "D:\path\to\client.dll" 0x12E11BC
```

常见 `client.dll` 位置：

| 用途 | 路径 |
|---|---|
| 独立版 / 旧版 | `<游戏根>\game\csgo\bin\win64\client.dll` |
| Steam 版 | `<SteamLibrary>\steamapps\common\Counter-Strike Global Offensive\game\csgo\bin\win64\client.dll` |

输出示例（节选）：

```
节表: .text(代码,17.9MB); .rdata(只读数据,5.6MB); .data(可写数据,3.7MB); .pdata(只读数据,1.0MB); ...
候选数: 1
  RVA 0x12E11BC  值=0.3250000  精确
  RVA 0x12E11BC → movss 引用 1 条（指令 RVA 0x4AB120）
  采信: RVA 0x12E11BC（唯一常量 + 唯一 movss 引用）
  期望: 0x12E11BC → 一致 [PASS]
  refinit   命中  1 处  唯一命中，RVA 差值 0  RVA=0x110EDB0
  regcore   命中  1 处  唯一命中，RVA 差值 0  RVA=0x110FAE0
  getfloat  命中  2 处  2 处命中 → 不唯一，拒用
结论: PASS
```

---

## 3. 两个真实版本的结果

### 1.40.4.5（CS2 独立版 `D:\cs2.10.30`）

| 项 | 结果 |
|---|---|
| 镜像 SizeOfImage | `0x1CA7000` |
| 节 | `.text` 17.9MB / `.rdata` 5.6MB / `.data` 3.7MB / `.pdata` 1.0MB |
| `0.325f` 命中 | 1 处 |
| 常量 RVA | `0x12E11BC` |
| movss 引用 | 1 条（指令 RVA `0x4AB120`） |
| 自动定位 | `0x12E11BC`，与实机已知值一致 |
| `refinit` | 唯一，`0x110EDB0` |
| `regcore` | 唯一，`0x110FAE0` |
| `getfloat` | 2 处命中，拒用（optional 降级） |
| 结论 | **PASS** |

### 1.41.9.x（Steam 正版）

| 项 | 结果 |
|---|---|
| 镜像 SizeOfImage | `0x29A3000` |
| 节 | `.text` 26.7MB / `.rdata` 7.1MB / `.data` 5.9MB / `.pdata` 1.5MB |
| `0.325f` 命中 | 1 处 |
| 常量 RVA | `0x1D12A38` |
| movss 引用 | 1 条（指令 RVA `0x10CCD96`） |
| 自动定位 | `0x1D12A38`，与实机已知值一致 |
| `refinit` | 唯一，`0x19B14E0`（对旧版 +`0x8A2730`） |
| `regcore` | 唯一，`0x1906030`（对旧版 +`0x7F6550`） |
| `getfloat` | 2 处命中，拒用（optional 降级） |
| 结论 | **PASS** |

---

## 4. 结论

1. **常量定位算法跨版本有效**：两个版本 `.text` 大小差 8.8MB、常量 RVA 差 `0xA31E7C`，
   算法没有改一行、没有改一个参数，都得到了与实机一致的地址。
2. **`refinit` / `regcore` 的字节签名跨这两个版本仍然唯一命中**，只是 RVA 变了 —— 说明签名本身是稳的，
   变的是位置，而位置由签名自动算，不需要人管。
3. **`getfloat` 的旧签名在新版彻底失效**（0 命中），放宽成通配后两版都变成 2 处命中、无法唯一区分。
   因此把它设计成 `optional`：不唯一就降级，只走 CounterStrikeSharp 的 `ConVar.Find()` 通道。
4. **风险边界明确**：算法成立的前提是"`0.325` 这个值不变"且"只有一条 movss 读它"。
   两条前提任一被打破，默认配置下闸门会拒绝写入（空转），不会写错内存。

---

## 5. 本报告不覆盖的部分

诚实说明，以下内容**没有**被离线验证，需要实机确认：

| 项 | 原因 |
|---|---|
| `refinit` / `regcore` 的**参数布局** | 签名只能证明"函数在哪"，不能证明"怎么调"。参数布局靠实机确认 |
| 运行时 `.data` 里可能出现的同值 float | 离线镜像是静态的；运行时若有代码写入 `0.325`，候选会变多。此时闸门会拒绝，日志会给出候选列表 |
| 非 Windows / 非 x64 环境 | 不支持，未做 |
| 专用服务器场景 | 未做 |
