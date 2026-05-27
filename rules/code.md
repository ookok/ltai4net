# rule: code
layer: L0
quality: 0.95
speed: 0.15
cost: 1.0
description: Code-related intent — programming, debugging, architecture

## keywords
代码
写
函数
bug
编译
编译错误
程序
算法
class
function
import
接口
调试
debug
重构
refactor
优化
optimize
架构
architecture
API
api
库
框架
依赖
单元测试
测试
代码质量
code quality
性能
performance
修复
fix
异常
exception

## regex
\bclass\s+\w+\s*[{(]
\bfunction\s+\w+\s*\(
\bimport\s+[\w.]+
\busing\s+[\w.]+;
\bnamespace\s+\w+
\bpublic\s+(class|interface|enum|struct|record)
