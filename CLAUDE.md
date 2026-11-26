# Hopkey — agent 说明

## 每次改完代码后的汇报要求

每次改动代码、完成构建后，最后的回复必须包含：

1. **改动了什么**：按功能/问题逐条说明，并列出改动的文件（可点击的相对路径）。
2. **build 的地址**：给出本次构建产物 exe 的完整绝对路径，例如
   `<仓库或 worktree 根目录>\src\Hopkey.App\bin\Debug\net10.0-windows\win-x64\Hopkey.exe`
   （在 worktree 中工作时，路径是 worktree 下的，不是主仓库下的）。
   并提醒：运行前先从托盘退出正在运行的 Hopkey。

## 构建与测试

- 构建：`dotnet build -c Debug`
- 自测：`dotnet run --project tests\Hopkey.SelfTest --no-build`（应全部通过，含性能指标）
