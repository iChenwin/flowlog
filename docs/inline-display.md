# FlowLog 行内呈现（inline display）设计规格与实现说明

范围：`flowlog/native` 的 C#/WPF/AvalonEdit 原生实现。

FlowLog 的行内呈现遵循「**保留原始 Markdown + 叠加视图元素与着色**」的核心原则，绝不破坏底层文本数据，以确保原生光标定位、文本选区、剪贴板交互以及 Undo/Redo 历史的绝对可靠与一致性：

> Offsets always refer to the unmodified Markdown document. Rendering never rewrites the source, so native selection, clipboard and undo keep working.
>
> — `native/Outline.cs`

---

## 1. 呈现架构与机制

| 组成 | 位置 | 机制与职责 |
| --- | --- | --- |
| `OutlinePresentationGenerator` | `native/Editor.cs` | 继承 `VisualLineElementGenerator`：在渲染层替换**可视字符宽度与展示元素**（如将大纲短横线替换为项目符号 `•  `、将箭头 `->`/`<-` 及提及 `@P` 等替换为矢量/Emoji 符号元素、将元数据行折叠为零宽占位符）。 |
| `OutlineColorizer` | `native/Editor.cs` | 继承 `DocumentColorizingTransformer`：对文档行进行**局部着色、字重改变与背景渲染**（粗体、斜体、删除线、下划线、高亮、行内代码背景、链接、任务关键字、标题与围栏代码）。 |
| `GuideLineRenderer` | `native/Editor.cs` | 实现 `IBackgroundRenderer`：在文本背景层绘制同级大纲块间的**虚线引导线（Dotted Guides）**。 |
| `FoldMarkerMargin` | `native/Editor.cs` | 继承 `AbstractMargin`：在行号左侧绘制带子块的大纲折叠/展开指示三角形。 |
| `FoldingManager` + `RefreshFoldings` | `native/Editor.cs` | 折叠管理：基于大纲缩进计算可折叠范围，折叠仅作用于可视布局，文档原始内容保持不变。 |
| `SourceMode` | `native/Editor.cs` | 纯源码视图模式：关闭所有替换生成器与边距修饰，显示行号，直接编辑裸 Markdown。 |

---

## 2. 支持的片段类型与呈现规范

### 2.1 块项目符号与层级缩进（Block Bullets & Indentation）

- **项目符号**：匹配行首 `- `（`Outline.BlockPattern = ^([ \t]*)- (.*)$`），将 `- ` 替换为可视宽度为 2 的项目符号 `•  `。
- **视觉缩进加宽**：`Outline.DisplayIndent` 将文档中的 Markdown 缩进（每级 2 空格）在视觉层等比例映射为 6 空格宽度的节奏（`VisualIndentSpaces = 6`），与 Logseq 视觉步长对齐，而文档底层依然保持标准 Markdown 2 空格缩进。

### 2.2 行内 Markdown 强调样式（Inline Emphasis）

行内强调样式由 `OutlineColorizer` 在保持源码字符可见的前提下进行富文本修饰：

| 语法 | 正则模式 | 视觉呈现效果 | 实现方式 |
| --- | --- | --- | --- |
| `**粗体**` | `\*\*([^\*\r\n]+)\*\*` | 加粗字体（Bold） | `Typeface` Weight 设置为 `FontWeights.Bold` |
| `*斜体*` | `(?<!\*)\*([^\*\r\n]+)\*(?!\*)` | 斜体样式（Italic） | `Typeface` Style 设置为 `FontStyles.Italic` |
| `~~删除线~~` | `~~([^\~\r\n]+)~~` | 中划线贯穿 | 反射应用 `TextDecorations.Strikethrough` |
| `_下划线_` | `_(?!_)([^_\r\n]+)_(?!_)` | 底部下划线 | 反射应用 `TextDecorations.Underline` |
| `==高亮==` | `(?<!=)==([^=\r\n]+)==(?!=)` | 亮黄色文字背景 | 反射应用 `backgroundBrush = #FFFF00` |
| `` `行内代码` `` | `` `([^`\r\n]+)` `` | 浅灰色代码底色 | 反射应用 `backgroundBrush = #F2F2F2` |

> 注：围栏代码块内部行（Code Fence）不触发上述行内强调样式解析。

行内反引号代码范围也保持字面量：其中的箭头、提及、链接及强调语法均不再解析；范围外的符号与样式照常呈现。浅灰代码背景与黄色高亮背景使用深色文字，在明暗主题下均保证可读性。

### 2.3 符号转换：箭头与提及标记（Glyphs & Mentions）

通过 `VisualLineElementGenerator` 生成可视元素替换源字符串，复制与持久化保持原始字符：

| 原始文本 | 语义含义 | 渲染呈现符号 | Unicode 码位 | 说明 |
| --- | --- | --- | --- | --- |
| `->` | 右箭头 | 🡒 | U+1F852 | 替换 2 字符宽度的文本为 Unicode 箭头符号 |
| `<-` | 左箭头 | 🡐 | U+1F850 | 替换 2 字符宽度的文本为 Unicode 箭头符号 |
| `@P` | 人员 (Person) | 👤 | U+1F464 | 个人信息/提及 |
| `@D` | 日期 (Date) | 📅 | U+1F4C5 | 日期标记 |
| `@T` | 时间 (Time) | 🕒 | U+1F552 | 时间标记 |
| `@L` | 位置 (Location) | 📍 | U+1F4CD | 地理位置/地点 |

### 2.4 页面链接、块引用与标签（Links, Block Refs & Tags）

- **语法模式**：`\[\[([^\]\r\n]+)\]\]|\(\(([0-9a-fA-F-]{36})\)\)|(?<![\w/#])#([^\s#\[\](),.;:!?]+)`
- **视觉着色**：整段文字着色为主题链接色（随浅色/深色主题动态计算）。
- **交互跟随**：
  - `Ctrl + 单击`：触发链接导航；
  - `[[页面名]]` / `#标签`：直接切换到对应 Page，若不存在则创建新页面；
  - `((uuid))`：遍历笔记查找目标块 ID 并跳转高亮对应行。

### 2.5 任务状态关键字（Task Keywords）

- **语法模式**：`^[ \t]*- (TODO|DOING|DONE)\b`
- **呈现样式**：
  - `TODO`：高亮链接色，SemiBold；
  - `DOING`：暗橙色（`Brushes.DarkOrange`），SemiBold；
  - `DONE`：弱化灰色（Muted），SemiBold。
- **快捷键交互**：光标所在行按 `Ctrl+Enter` 在 `TODO -> DOING -> DONE -> (清除)` 之间循环切换。

### 2.6 标题行（Headings）

- **语法模式**：`^[ \t]*- (#{1,6})\s+(.+)`
- **呈现样式**：仅对以 `- ` 开头的大纲标题生效；`#` 起至行尾字号放大至 **19px** 并应用 SemiBold 字重。

### 2.7 整行代码围栏（Code Fences）

- 支持 ``` 与 `~~~` 闭合围栏；
- 围栏内的代码行采用等宽字体（`Cascadia Mono, Consolas, Microsoft YaHei UI`）与弱化灰底呈现；
- 围栏内代码保持字面量纯文本，不执行任何大纲符号替换、强调色或提及解析。

### 2.8 块属性行（Metadata / Properties）

- **语法模式**：`^\s*[\w-]+::(?:\s.*)?$`（如 `id:: ...`、`title:: ...`）
- **折叠与展示**：
  - **光标不在该行时**：整行文本被替换为零宽字符 `\u200B`，并在视觉上隐藏以保持大纲整洁；
  - **光标聚焦到该行时**：立即解除隐藏并恢复全文可见，方便直接编辑原始属性。

---

## 3. 大纲引导线与折叠渲染（Guides & Folding）

1. **同级虚线引导线（Dotted Sibling Guides）**：
   - 由 `GuideLineRenderer` 在文本层下方的 `KnownLayer.Selection` 层绘制；
   - 虚线连接具有相同父级的相邻兄弟节点，折叠某一父节点后，其子层级的所有引导线自动隐藏。
2. **折叠指示器（Fold Markers）**：
   - 仅对实际拥有子块的大纲行绘制三角形指示符；
   - **展开状态**：向下实心三角形；
   - **折叠状态**：向右实心三角形；
   - 支持鼠标单击或 `Ctrl+.` 快捷键切换折叠/展开。

---

## 4. 呈现与文档原文的一致性保证

| 视觉行为 | 是否修改底层文档内容 | 复制与保存输出 |
| --- | --- | --- |
| 项目符号 `•  ` | 否 | `- ` |
| 缩进视觉加宽 | 否 | 原生 2 空格 / Tab |
| 行内强调（粗斜体/删除线/下划线/高亮/代码底色） | 否 | 原样保留 Markdown 标记字符 |
| 箭头 `🡒`/`🡐` 与提及 `👤`/`📅`/`🕒`/`📍` | 否 | 恢复为 `->` / `<-` / `@P` / `@D` / `@T` / `@L` |
| 属性行零宽折叠 | 否 | 完整保留 `key:: value` 文本 |
| 大纲折叠 | 否 | 不删除或裁剪被折叠的内容 |
| 大纲引导线与手柄 | 否 | 属于外部装饰层，不影响文本 |

---

## 5. 对比 Logseq 桌面版

| 语法/片段 | Logseq 桌面版（AST / Web DOM） | FlowLog 原生版（WPF / AvalonEdit） |
| --- | --- | --- |
| `**粗体**` / `*斜体*` | 转换为 HTML `<b>` / `<i>` 节点 | 保留标记字符，基于 TextRunProperties 叠加样式 |
| `~~删除线~~` / `_下划线_` | 转换为 `<del>` / `<u>` 节点 | 反射设置 TextDecorations |
| `==高亮==` / `` `代码` `` | 转换为 `<mark>` / `<code>` 节点 | 动态渲染背景画刷（黄色高亮 / 浅灰代码底） |
| 箭头 `->`/`<-` | 符号替换或自定义渲染 | VisualLineElementGenerator 替换为 Unicode 箭头字形 |
| 提及 `@P`/`@D`/`@T`/`@L` | 自定义 Tag 组件 | VisualLineElementGenerator 替换为 Emoji 图标 |
| `[[页面]]` / `#标签` | React/Rum 组件，支持悬停预览 | 颜色高亮 + Ctrl+单击导航/自动建页 |
| `((uuid))` | 块嵌入/引用组件 | 链接着色 + 全库 UUID 查找定位 |
| 属性行 `key:: val` | 表格或键值对折叠面板渲染 | 光标离开自动零宽隐藏，光标进入显示全文 |
