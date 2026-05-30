---
name: paper-format
description: 论文排版——学术写作格式/引文规范/LaTeX 模板/GB/T 7714
license: MIT
---

# Paper Format 论文排版

辅助学术论文写作与排版。

## 1. 中文论文结构
```
标题（20 字内）
作者
摘要（300 字内）：背景→问题→方法→结果→结论
关键词（3-5 个）
1 引言
2 相关工作
3 方法
4 实验
5 结论
参考文献
```

## 2. 英文论文结构（IEEE/ACM）
```
Title
Abstract (150-250 words)
Keywords
1. Introduction
2. Related Work
3. Methodology
4. Experiments
5. Conclusion
References
```

## 3. 参考文献格式（GB/T 7714）
```
专著: [序号] 作者. 书名[M]. 出版地: 出版社, 年份.
期刊: [序号] 作者. 题名[J]. 刊名, 年, 卷(期): 起止页码.
会议: [序号] 作者. 题名[C]//会议名. 出版地: 出版社, 年份: 起止页码.
网络: [序号] 作者. 题名[EB/OL]. (发布日期)[引用日期]. URL.
```

## 4. LaTeX 模板
```latex
\documentclass[12pt,a4paper]{ctexart}
\usepackage{geometry,graphicx,booktabs}
\geometry{left=2.5cm,right=2.5cm,top=2.5cm,bottom=2.5cm}

\title{论文标题}
\author{作者}
\date{\today}

\begin{document}
\maketitle
\begin{abstract}
摘要内容...
\end{abstract}

\section{引言}
...
\end{document}
```

## 5. 图表规范
- 图: 矢量图（PDF/SVG），分辨率 ≥ 300dpi
- 表: 三线表（`\toprule \midrule \bottomrule`）
- 图表编号: 图 1、表 1 独立编号
- 交叉引用: `\label{fig:xxx}` `\ref{fig:xxx}`
