# VMRecoil

把 CS2 的 `viewmodel_recoil` 做成一个**真正可用的 cvar**（`1` 开 / `0` 关），并自动持久化。

CS2 客户端原本把视角后坐力缩放写死成 `client.dll` 里的一个 float 常量（`0.325f`），而且不再是可调 cvar。
本插件在进程内存里改写这个常量（**不动磁盘上的 `client.dll`**），同时通过 CounterStrikeSharp 把
`viewmodel_recoil` 重新注册成真实 float cvar，让它在控制台可见、可改、可存档。

| 项 | 值 |
|---|---|
| 插件名 | `VMRecoil` |
| 依赖 | CounterStrikeSharp 1.0.284+ |
| 目标框架 | .NET 8 |
| 定位方式 | RVA 直读 + AOB 签名反算双重校验 |
| 设置文件 | `<插件目录>\vmrecoil_user.cfg` |

---

## 功能

- `viewmodel_recoil 1`：恢复原版（常量写回 `0.325f`）
- `viewmodel_recoil 0`：关闭视角武器后坐力（常量写 `0.0f`）
- 取值区间 `[0,1]`，中间值按比例缩放（如 `0.5` = 一半后坐力）
- 改动后自动写入 `vmrecoil_user.cfg`，换图 / 重载插件 / 重开游戏自动恢复
- 卸载（`css_plugins unload VMRecoil`）时把常量恢复成 `0.325f`，不会留下"后坐力永久消失"的坑

> 说明：`viewmodel_recoil` 只影响**视角武器模型的后坐力表现**，不改变子弹散布、不改变准星回弹（那部分由 `weapon_recoil_*` 一类参数控制）。

---

## 工作原理

1. **定位常量**：先按内置 RVA（`0x12E11BC`）直读，值必须是 `0.325f`；
   若不符（例如客户端更新导致偏移变化），再走 AOB 签名扫描 `.text`，反算出地址后与 RVA 比对，
   两者一致才采信；否则**放弃改动，插件空转**，不会瞎写内存。
2. **注册 cvar**：用从 `client.dll` 内按签名定位到的引擎 cvar 注册三件套
   （`cvar.refinit` / `cvar.regcore` / `cvar.getfloat`）注册真实 float cvar，
   走引擎原生 cvar 表，因此不会触发 Panorama 相关闪退。
3. **轮询自愈**：每 tick 以 cvar 值为唯一真值，比对内存常量与期望值，漂移自动纠正。
4. **写内存**：`VirtualProtect` 按整页临时改为 RWX，写入后 `finally` 还原，避免 4 字节跨页问题。

---

## 安装

1. 服务器需已装 **Metamod:Source** + **CounterStrikeSharp**（本插件不自动加载 Metamod）。
2. 编译产物 `VMRecoil.dll` 放到：

   ```
   <服务器根>\game\csgo\addons\counterstrikesharp\plugins\VMRecoil\VMRecoil.dll
   ```

3. 启动服务器（本地调试建议 `-insecure`），控制台执行：

   ```
   viewmodel_recoil 0     // 关闭视角后坐力
   viewmodel_recoil 1     // 恢复原版
   ```

4. 首次运行日志在 CounterStrikeSharp 的 `logs\` 目录，前缀 `[VMRecoil]`。

---

## 编译

需要 .NET 8 SDK。

```bat
build.cmd
```

或手动：

```bat
dotnet build src\VMRecoil.csproj -c Release
```

默认从 NuGet 拉 `CounterStrikeSharp.API 1.0.284`。想用本地服务器自带的 API：

```bat
dotnet build src\VMRecoil.csproj -c Release -p:CssApiDir="D:\你的服务器\game\csgo\addons\counterstrikesharp\api"
```

产物：`src\bin\Release\VMRecoil.dll`

---

## 兼容性与限制

- **强依赖 client.dll 版本**。当前常量 RVA 与签名基于 `client.dll 1.40.4.5`
  （SHA1 `A7A6BD4A53A31DFFBA2F89A5AAD57D0C278D6638`）。客户端更新后若 RVA 失效，
  插件会退到签名扫描；签名也失效时插件空转并打日志，不会误写。
- 只在本地/`-insecure` 服务器环境验证过；**线上使用风险自负**（改内存行为在任何反作弊环境下都属高危）。
- 不改磁盘文件、不注入外部进程（插件本身跑在服务器进程内）。
- 仅 x64 Windows。

---

## 目录结构

```
viewmodel_recoil/
├── src/
│   ├── VMRecoilPlugin.cs     插件主体
│   ├── AobLocator.cs         签名定位层
│   └── VMRecoil.csproj
├── examples/
│   └── vmrecoil.cfg          示例配置
├── docs/
│   └── CHANGELOG.md
├── build.cmd
├── README.md
├── LICENSE
├── .gitignore
└── .gitattributes
```

## License

MIT，见 [LICENSE](LICENSE)。
