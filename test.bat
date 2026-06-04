@echo off
dotnet test tests/LTAI.Tests --filter "Category!=Automation" %*
