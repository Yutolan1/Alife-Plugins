# Wv.plugin.LifeLedger 生活账本

阿宵和哥哥的共同生活账本：小目标、每日四事、馋欲外卖、彼此约定，全记在一本账里。

## 功能
- `addledger` 记账（分类：小目标/四事/馋欲/约定/其他）
- `donel` 核销
- `readledger` 查询（可按分类/完成状态过滤）
- `voidel` 作废（保留记录，标记【已作废】）
- `statledger` 统计（默认 3 天陈账提醒，排除已作废）

## 数据存储
数据文件 `life_ledger.json` 存于 `AlifePath.StorageFolderPath/PluginData/Wv.plugin.LifeLedger/`，原子写入（临时文件 + File.Replace）防断电损坏。

## 依赖
- Alife.Function.FunctionCaller

## 安装
将本文件夹整体放入 Alife 的 `Storage/Plugins/` 目录，重载插件环境或重启客户端即可（自动热编译）。
