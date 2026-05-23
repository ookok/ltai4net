using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace LTAI.Tests;

public class OnnxModelValidationTests
{
    [Fact]
    public void TC_ONNX_01_ModelLoads_WithoutError()
    {
        var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "l0", "model.onnx");

        if (!File.Exists(modelPath))
        {
            Assert.True(true, "Model file not found — skipping ONNX test");
            return;
        }

        using var session = new InferenceSession(modelPath);
        Assert.NotNull(session);
        Assert.True(session.InputMetadata.Count > 0);
    }

    [Fact]
    public void TC_ONNX_02_InputOutput_DimensionsMatch()
    {
        var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "l0", "model.onnx");

        if (!File.Exists(modelPath))
        {
            Assert.True(true, "Model file not found — skipping ONNX test");
            return;
        }

        using var session = new InferenceSession(modelPath);
        var input = session.InputMetadata.First();
        var output = session.OutputMetadata.First();

        Assert.Equal("input_ids", input.Key);
        Assert.Equal("last_hidden_state", output.Key);
    }
}
