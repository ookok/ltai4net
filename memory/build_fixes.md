# memory: build_fixes
domain: code
confidence: 0.85
version: 1.0.0

## summary
Common build error patterns and their proven fixes across the LTAI codebase.

## facts
- CS0103_name_not_found: Usually missing using directive or namespace — check imports first (confidence: 0.90)
- CS0246_type_not_found: Either missing project reference in .csproj or missing using — check both (confidence: 0.90)
- CS1061_method_not_found: API surface change — verify the method exists on the type, check for extension methods (confidence: 0.85)
- csproj_ref: Test projects often have stale project references after module renames — update .csproj ProjectReference (confidence: 0.85)
- nullable_warnings: CS8618/CS8602 — add required modifier, make field nullable, or add null check (confidence: 0.90)
- avalonia_api: Avalonia controls may not have WPF-like properties — use ScrollViewer wrapper, TextChanged event instead of GetPropertyChangedObservers (confidence: 0.80)

## context
Build errors most commonly occur after project restructuring (module renames, new file additions, dependency changes). The dotnet build + CSharpCompilationService dual approach catches both syntax and semantic errors.

## tags
- build
- errors
- fixes
- compilation

## triggers
- pattern: "build error" (weight: 1.0)
- pattern: "compilation error" (weight: 0.9)
- pattern: "CS0" (weight: 0.8)
