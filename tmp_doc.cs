using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using UglyToad.PdfPig;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using LTAI.AI;
using LTAI.Core;
using LTAI.Agent.Vector;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

[ToolDomain("office")]
public sealed class DocumentTools
{
    private readonly string _ws;
    private readonly KbGraph? _kbGraph;
    private readonly ILogger<DocumentTools>? _logger;

    public DocumentTools(string ws, KbGraph? kbGraph = null, ILogger<DocumentTools>? logger = null)
    {
        _ws = ws;
        _kbGraph = kbGraph;
        _logger = logger;
    }

