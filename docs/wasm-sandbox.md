# Wasmtime Sandbox — Multi-Language WASM Execution

## Overview

LTAI integrates Wasmtime (v44+) for sandboxed WebAssembly execution.
Any language that compiles to `.wasm` with WASI support can run inside the sandbox.

## Supported Languages

| Language | Compiler | Example |
|---|---|---|
| Rust | `wasm32-wasi` target | `rustc --target wasm32-wasi` |
| AssemblyScript | `asc` | `asc hello.ts -o hello.wasm` |
| TinyGo | `tinygo build -target=wasi` | `tinygo build -o hello.wasm -target=wasi main.go` |
| C/C++ | `clang --target=wasm32-wasi` | `clang -o hello.wasm --target=wasm32-wasi hello.c` |
| Python | `wasm-pack` (via Rust binding) | Pyodide-based |
| Any language | Via WASM toolchain | Must emit WASI-compatible `.wasm` |

## How It Works

1. Agent calls `ExecuteWasmAsync(wasmPath)`
2. Wasmtime loads the `.wasm` binary (cached in memory)
3. WASI configuration applied:
   - Read-only workspace access
   - No network
   - 60-second timeout
4. Module executes sandboxed
5. stdout captured and returned

## Examples

### Rust (hello.wasm)

```rust
fn main() {
    println!("Hello from WASM sandbox!");
}
```

```bash
rustc --target wasm32-wasi hello.rs -o hello.wasm
# Then in LTAI: /run wasm hello.wasm
```

### TinyGo (math.wasm)

```go
package main

import "fmt"

func main() {
    fmt.Println("42")
}
```

```bash
tinygo build -o math.wasm -target=wasi math.go
```

## Security Model

- **No network**: WASI networking is explicitly disabled (`--env PATH=""`)
- **Read-only**: Workspace mapped as read-only
- **Timeout**: Hard limit of 60 seconds
- **Module cache**: Re-compiled modules are cached by filename (`ConcurrentDictionary`)
- **Fallback**: When Wasmtime engine unavailable, falls back to restricted shell execution
