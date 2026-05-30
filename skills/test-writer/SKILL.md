---
name: test-writer
description: 单元测试编写——按 Arrange-Act-Assert 模式生成 xUnit 测试
license: MIT
---

# Test Writer 测试编写

为指定的 C# 方法或类生成 xUnit 测试。

## 测试结构

每个测试方法遵循 Arrange-Act-Assert 模式：

```csharp
[Fact]
public void MethodName_Scenario_ExpectedResult()
{
    // Arrange
    // Act
    // Assert
}
```

## 覆盖要求

1. **正常路径** — 最常用的输入，验证正确输出
2. **边界条件** — 空值、空集合、最大值、最小值
3. **异常路径** — 预期抛出的异常、错误处理
4. **边缘情况** — 特殊字符、并发、超时

## 命名规范

`{MethodName}_{Scenario}_{ExpectedResult}`

示例：
- `CreateUser_ValidInput_ReturnsSuccess`
- `CreateUser_DuplicateEmail_ThrowsException`
- `GetUser_NegativeId_ReturnsNull`
