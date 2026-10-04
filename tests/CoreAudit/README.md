# 共享核心与持久化审查回归

这个独立项目直接编译共享源码，使用自己的 `bin/obj`，不会并发写入 `core`、Android 或桌面工程的构建目录。父 `CoreTests.csproj` 已排除 `CoreAudit/**`，避免递归编入本项目的程序集属性文件。

```powershell
dotnet run --project tests/CoreAudit/CoreAudit.csproj -- reports/core-audit-after.json
```

9 组回归包括：共同句单句确认的快照、历史、书签与磁盘恢复；已选支线单句恢复；非法单句状态拒绝；主导航缺失时保留备份并阻止旧版迁移；主导航校验失败时保留最新书签；合法仅书签文档首次迁移；损坏备份的证据保留；ZIP 提交前后取消的事务边界。导航序列测试使用 schema 1/2/3、6 个固定随机种子、每个种子 300 步，共 5,400 步，每一步都检查导出后静音恢复、位置、履历和暂停状态。

配音包和进度均为程序创建的合成样本。临时目录使用 `pgr-core-audit-` 加 GUID，递归清理前重新验证绝对路径、父目录和文件名；不读取或写入用户进度，不修改真实配音包。

修复前的失败证据分别在 `reports/core-audit-before.json`、`reports/core-audit-progress-before.json` 与 `reports/core-audit-bookmark-before.json`；最新结果在 `reports/core-audit-after.json`。这些仅证明共享核心逻辑和文件事务，不代表安卓设备、真实游戏、音频听感或性能验收。
