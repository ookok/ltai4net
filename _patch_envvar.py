# Update FindBaseModelsDirectory to check LTAI_EMBEDDING_MODELS_DIR env var
import sys

with open('src/LTAI.AI/LocalEmbedder.cs', 'rb') as f:
    data = f.read()

# Use \r\n line endings matching the file
old = (
    b'    private static string? FindBaseModelsDirectory()\r\n'
    b'    {\r\n'
    b'        string[] candidates =\r\n'
    b'        [\r\n'
    b'            Path.Combine(AppContext.BaseDirectory, "models")'
)

new_insert = (
    b'    private static string? FindBaseModelsDirectory()\r\n'
    b'    {\r\n'
    b'        // P17.3: env var override (CI / shared cache / offline).\r\n'
    b'        var envDir = Environment.GetEnvironmentVariable("LTAI_EMBEDDING_MODELS_DIR");\r\n'
    b'        if (!string.IsNullOrEmpty(envDir) && Directory.Exists(envDir))\r\n'
    b'            return Path.GetFullPath(envDir);\r\n'
    b'\r\n'
    b'        string[] candidates =\r\n'
    b'        [\r\n'
    b'            Path.Combine(AppContext.BaseDirectory, "models")'
)

if old in data:
    data = data.replace(old, new_insert, 1)
    with open('src/LTAI.AI/LocalEmbedder.cs', 'wb') as f:
        f.write(data)
    print('OK: replaced 1 occurrence')
else:
    print('ERROR: old string not found')
    sys.exit(1)
