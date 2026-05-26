# prompt: build_verify_loop
domain: build
description: Build and verify loop with fix iteration

## template
Execute the following build-verify loop:

Step 1: Run the build command for the project
Step 2: Collect and analyze any errors or warnings
Step 3: If there are errors:
  - Identify the root cause
  - Fix the issue in the source files
  - Return to Step 1
Step 4: If build passes, run tests
Step 5: Fix any test failures and return to Step 1

Max iterations: {{max_iterations}}
Project path: {{project_path}}

## variables
- max_iterations: Max build-fix-test iterations (default: 5)
- project_path: Path to the project (required)

## triggers
- pattern: "build and verify" (weight: 1.0)
- pattern: "build fix loop" (weight: 0.9)
- pattern: "compile and test" (weight: 0.8)

## tags
- build
- verify
- loop
