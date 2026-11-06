# VMRecoilAuto — 长期做法版（不用跟着每版 client.dll 改地址）

> 这个目录是 VMRecoil 的**另一个版本**，和仓库根目录的 `VMRecoil`（v2.x）**并存、互不影响**。
> 你可以只用其中一个，也可以两个都不装。
>
> 一句话区别：
> **v2.x 里写死了 `0x12E11BC`；v3 里一个地址都没有。**

---

## 目录

- [为什么要有这个版本](#为什么要有这个版本)
- [它和根目录的 v2.x 到底差在哪](#它和根目录的-v2x-到底差在哪)
- [它是怎么找到那个常量的](#它是怎么找到那个常量的)
- [安全闸门：宁可不动，也不瞎写](#安全闸门宁可不动也不瞎写)
- [跨版本实测数据](#跨版本实测数据)
- [编译](#编译)
- [安装](#安装)
- [使用](#使用)
- [配置文件 vmrecoil_auto.json](#配置文件-vmrecoil_autojson)
- [故障排查](#故障排查)
- [客户端又更新了怎么办](#客户端又更新了怎么办)
- [已知限制](#已知限制)

---

## 为什么要有这个版本

CS2 客户端把视角后坐力缩放写死在 `client.dll` 的 `.rdata` 里，是一个 `float 0.325f`。
v2.x 用 **固定 RVA + AOB 签名**定位它 —— 只要 Valve 一发版，地址就变，插件必须重编译。

实测两个版本：

| 版本 | 常量 RVA |
|---|---|
| 1.40.4.5 | `0x12E11BC` |
| 1.41.9.x | `0x1D12A38` |

差了一千多万字节。v2.x 遇到新版本会**空转**（不改内存、不崩），但也就没用了。

v3 换了个思路：**忘掉地址，记住"长什么样"**。

---

## 它和根目录的 v2.x 到底差在哪

| 项 | v2.x（根目录 `src/`） | v3（本目录 `auto/src/`） |
|---|---|---|
| 常量定位 | 硬编码 RVA `0x12E11BC` + 22 字节 AOB 交叉校验 | **值指纹（0.325f）+ movss 引用反查**，地址零硬编码 |
| 换版本要重编译吗 | 要 | **不要**（除非 Valve 改了 0.325 这个数值，改 JSON 即可） |
| cvar 三件套签名 | 写在 `AobLocator.cs` 里，改一次要重编译 | 写在进程外的 `vmrecoil_auto.json`，**改完重载插件即生效** |
| cvar 名 | 固定 `viewmodel_recoil` | 配置文件里可改 |
| 开关写入的值 | 固定 `0.325f` / `0.0f` | 配置文件里可改（`onValue` / `offValue`） |
| 上次定位结果 | 无 | 记进状态文件，热重载 / 二次加载时先验值复用 |
| 功能行为 | 真实 cvar、轮询自愈、持久化、卸载恢复 | **完全一致** |
| 插件名 / DLL | `VMRecoil` | `VMRecoilAuto`（可同时装，互不冲突） |

一句话：**功能一模一样，定位方式换成了能活更久的做法。**

---

## 它是怎么找到那个常量的

三步，全部在内存里做，不动磁盘文件。

### 第 1 步：值指纹

把 `client.dll` 的**可读节**扫一遍，找"哪 4 个字节的 float 值等于 `0.325f`"（默认容差 `1e-4`）。

为什么要扫全部节而不只 `.rdata`？因为没人能保证 Valve 下次还把它放在 `.rdata`。

实测两个版本都只命中 **1 处**，这就是"指纹"的由来。

### 第 2 步：引用反查

一个 float 常量不会自己起作用，一定有人读它。所以：

- 线性扫描 `.text`，找所有 `movss xmm, [rip+disp32]`（`F3 0F 10` / `F3 0F 11` 且 ModRM 为 RIP 相对）；
- 把每条指令的目标地址算出来，跟第 1 步的候选逐一比对；
- 数一数：**有几条指令在读这个常量**。

实测两个版本都**只有 1 条** `movss` 读它 —— 这正是"视角后坐力缩放系数"该有的样子（只被一处用）。

### 第 3 步：三道闸门都过了才采信

```
候选值 == 0.325f ± 容差     ← 值对
候选唯一（全镜像 1 处）      ← 不歧义
读它的 movss 唯一（1 条）    ← 结构对
```

三条全过 → 写进内存；任何一条不过 → **一个字节都不写**，日志说明原因。

> 为什么加"引用唯一"这道？因为单靠值指纹可能撞车（别的模块也可能有 0.325）。
> 加上"只被一条 movss 读"这个结构约束，误判概率就低到可以忽略。

### 热重载 / 二次加载

如果常量此刻已被改成 `0.0f`（比如重载插件时插件正处在"关"状态），值指纹就搜不到 `0.325` 了。
所以定位成功后会把这个 RVA 记进状态文件：

```
# <插件目录>\vmrecoil_auto_user.cfg
viewmodel_recoil 0
const_rva 0x12E11BC
```

下次加载先读这行、**验值再复用**（值必须是 `onValue` 或 `offValue` 之一）。
验不过（客户端更新了、地址被别的模块占了）就丢弃，重新走值指纹。

---

## 安全闸门：宁可不动，也不瞎写

这是本插件唯一的"性格"：**不确定就不干**。

| 情况 | 行为 |
|---|---|
| 值指纹 0 命中 | 空转，日志明确说"镜像里找不到 0.325，可能 Valve 改值了" |
| 值指纹多命中 | 默认放弃，日志列出所有候选和各自引用数 |
| 引用数 ≠ 1 | 默认放弃 |
| `VirtualProtect` 失败 | 报错返回，不重试 |
| 写入后回读不一致 | 重试 1 次，仍不一致则报错并保持原状态 |
| 任何未捕获异常 | `Load` 整体 try/catch，异常即空转 |

**不会发生的事**：随机挑一个候选硬写；写失败继续轮询疯狂重试；把不确定的地址当真的用。

---

## 跨版本实测数据

用本目录的 `test/`（离线自检工具）对两个真实 `client.dll` 跑同样的算法：

| 项 | 1.40.4.5（27.9 MB） | 1.41.9.x（39.2 MB） |
|---|---|---|
| 镜像 SizeOfImage | `0x1CA7000` | `0x29A3000` |
| `.text` 大小 | 17.9 MB | 26.7 MB |
| `0.325f` 值指纹命中 | **1 处** | **1 处** |
| 常量 RVA | `0x12E11BC` | `0x1D12A38` |
| 指向它的 movss | **1 条**（指令 RVA `0x4AB120`） | **1 条**（指令 RVA `0x10CCD96`） |
| 自动定位结果 | `0x12E11BC` ✅ 与实机已知值一致 | `0x1D12A38` ✅ 与实机已知值一致 |
| `refinit` 签名 | 唯一命中 `0x110EDB0` | 唯一命中 `0x19B14E0` |
| `regcore` 签名 | 唯一命中 `0x110FAE0` | 唯一命中 `0x1906030` |
| `getfloat` 签名 | 2 处命中 → 拒用（自动降级） | 2 处命中 → 拒用（自动降级） |

**结论：常量定位算法跨这两个版本无需任何改动即可得到与实机一致的地址。**

复现方法见 [`docs/VERIFICATION.md`](../docs/VERIFICATION.md)。

---

## 编译

需要 .NET 8 SDK。

```bat
cd auto
build.cmd
```

产物：`auto\src\bin\Release\VMRecoilAuto.dll`

也可以直接：

```bat
dotnet build auto\src\VMRecoilAuto.csproj -c Release
```

---

## 安装

1. 目标环境需已装 **Metamod:Source** + **CounterStrikeSharp 1.0.284+**（本插件不负责装它们）。
2. 把 `VMRecoilAuto.dll`（以及同目录产出的依赖 DLL）放到：

   ```
   <服务器根>\game\csgo\addons\counterstrikesharp\plugins\VMRecoilAuto\
   ```

3. 重启服务器或 `css_plugins load VMRecoilAuto`。
4. 首次运行会在插件目录自动生成 `vmrecoil_auto.json`（配置）。

> 和根目录的 `VMRecoil` 可以同时安装：插件名不同、DLL 不同、配置文件不同、cvar 名默认相同。
> 如果两个都装且都用 `viewmodel_recoil` 这个名字，谁后加载谁生效 —— **建议只装一个**。

---

## 使用

```
viewmodel_recoil 0      # 关闭视角后坐力（常量写 0.0）
viewmodel_recoil 1      # 恢复原版（常量写 0.325）
viewmodel_recoil 0.5    # 中间值，按比例缩放
```

- 值会被写进 `vmrecoil_auto_user.cfg`，换图 / 重载 / 重开游戏自动恢复；
- 卸载插件时会把常量恢复成 `onValue`（默认 `0.325`），不会留下"后坐力永久消失"。

只想验证插件在干活、不想改设置？看日志里的 `心跳` 行，每 30 秒一条：

```
[VMRecoilAuto] 心跳 cvar=0（css）常量=0.000 state=0 设置=0 定位=值指纹唯一命中 + movss 引用唯一（RVA 0x12E11BC...）
```

---

## 配置文件 vmrecoil_auto.json

首次运行自动生成在插件目录。支持 `//` 注释和尾随逗号。**改完保存 → `css_plugins reload VMRecoilAuto` 即生效，不用重编译。**

```jsonc
{
  "cvarName": "viewmodel_recoil",   // 控制台命令名
  "onValue": 0.325,                 // 原版常量值：必须与 client.dll 里那个 float 完全一致
  "offValue": 0.0,                  // "关"时写入的值
  "locate": {
    "valueTolerance": 0.0001,       // 值比对容差
    "requireUniqueConstant": true,  // 常量必须全镜像唯一
    "requireUniqueReference": true, // 读它的 movss 必须唯一
    "rvaHints": []                  // 可选：已知地址优先尝试，例如 ["0x12E11BC"]
  },
  "cvarSignatures": {
    "refinit":  { "pattern": "48 89 5C 24 08 ...", "baselineRva": "0x110EDB0" },
    "regcore":  { "pattern": "48 89 5C 24 08 ...", "baselineRva": "0x110FAE0" },
    "getfloat": { "pattern": "40 53 48 83 EC ...", "baselineRva": "0x110DA0", "optional": true }
  }
}
```

| 字段 | 说明 |
|---|---|
| `cvarName` | 控制台命令名，不能含空格 |
| `onValue` | 原版常量值。**Valve 哪天真改了 0.325，只改这里** |
| `offValue` | 关闭时写入的值，一般 `0.0` |
| `valueTolerance` | 建议 `1e-4`，上限 `0.01` |
| `requireUniqueConstant` | 默认 `true`。设 `false` 可放宽到"多候选时找唯一引用的那个"，不推荐 |
| `requireUniqueReference` | 默认 `true`。同上，不推荐关 |
| `rvaHints` | 纯加速用。填了会先试这个地址（**仍要验值**），不填完全自动 |
| `cvarSignatures.*.pattern` | 字节 hex，空格分隔，`??` 表示通配 |
| `cvarSignatures.*.baselineRva` | 只用于日志显示"RVA 差值"，不影响选择 |
| `cvarSignatures.*.optional` | `true` = 这条签名找不到/不唯一时只降级不阻断。`getfloat` 默认就是 `true` |

### 关于 `getfloat` 为什么是 optional

`getfloat` 只是"第二读数通道"（`ref+8 → ConVar → float`）。主通道是 CounterStrikeSharp 的 `ConVar.Find()`，
实测可用。所以 `getfloat` 签名不唯一/失效时，插件会自己降级，日志里写一行警告，**功能不受影响**。

`refinit` 和 `regcore` 是注册 cvar 必需的，它们失效时不注册 cvar（但常量改写照常工作）。

---

## 故障排查

按日志关键字查表：

| 日志关键字 | 含义 | 该怎么办 |
|---|---|---|
| `值指纹无命中` | 镜像里找不到 `onValue` 这个 float | 1) 确认 `onValue` 是否还等于原版常量（Valve 改了值就跟着改）；2) 确认插件改的确实是这一版 client.dll |
| `指纹不唯一，主动放弃` | 有多处同值常量 | 日志会列出候选及引用数。确认后可以把唯一引用的那个 RVA 写进 `rvaHints` |
| `状态文件记录的 RVA ... 已不合法` | 客户端更新了，旧地址失效 | **正常现象**，插件会自动重新定位，不用管 |
| `cvar 注册链签名不可用` | `refinit`/`regcore` 签名失效 | 见下一节，改 JSON 里的 pattern |
| `getfloat 通道不可用` | 预期内降级 | 不用管 |
| `cvar 未进入引擎 cvar 表` | 注册没成功，控制台会把它当未知命令 | 看上面的注册失败原因 |
| `卸载：常量已恢复` | 正常卸载流程 | —— |
| `未能定位 ... 本次不会改动任何内存` | 空转保护生效 | 结合上面的具体原因处理 |

---

## 客户端又更新了怎么办

**情况 A：只是地址变了（最常见）**

不用做任何事。插件每次加载都重新定位，日志里 `状态文件记录的 RVA ... 已不合法，改为自动定位` 就是它在自我更新。

**情况 B：Valve 改了 0.325 这个数值**

1. 用任意工具（IDA / x64dbg / 自己写脚本）在 `client.dll` 里确认新的常量值；
2. 改 `vmrecoil_auto.json` 里的 `onValue`；
3. `css_plugins reload VMRecoilAuto`。

不用重编译。

**情况 C：`refinit` / `regcore` 签名失效**

1. 在 IDA 里找 `ConVarRef` 的初始化函数（特征：写 `-1` 到 `[rcx]`、清零 `[rcx+8]`、参数是 `(ConVarRef*, int, int16)`）；
2. 取函数开头 20~30 字节，转成 `XX XX XX ...`；
3. 填进 `cvarSignatures.refinit.pattern`（`??` 可通配相对调用/偏移）；
4. 保存 → `css_plugins reload VMRecoilAuto`。

同样不用重编译。

> 想省事：把新签名提 issue / PR 上来，这个文件就是为了让签名可以被别人补而存在的。

---

## 已知限制

1. **依赖"0.325 这个数值不变"**。Valve 一旦改值，需要改 `onValue`（改 JSON 即可）。
2. **依赖"该常量只被一条 movss 读"**。如果哪天 Valve 把这段逻辑复制成两份，闸门会拒绝写入（安全 > 可用）。
3. **cvar 注册依赖引擎内部调用约定**。`refinit`/`regcore` 的**参数布局**没法靠签名验证，只能实机确认；
   若某版改了参数布局，表现为 cvar 注册后读不到值/游戏崩溃，此时应关闭 cvar 注册（不装即不注册）。
4. **只验证过客户端侧**（本地 listen server / 客户端插件）。专用服务器上的 `client.dll` 是客户端模块，行为可能不同。
5. **不改磁盘文件**。所有修改都在进程内存，重启即还原。

---

## 许可

与仓库根目录一致（见 [`../LICENSE`](../LICENSE)）。