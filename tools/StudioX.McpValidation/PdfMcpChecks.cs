using System.Text;
using System.Text.Json;
using StudioX.Application.Mcp;

internal static class PdfMcpChecks
{
    public static async Task RunAsync(StudioXMcpSession session, SwitchingAuthorizer authorizer,
        string project, string temporaryRoot, Action<bool, string> check)
    {
        var names = (await session.ListToolsAsync()).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        check(names.Contains("pdf_list") && names.Contains("pdf_inspect") && names.Contains("pdf_page"),
            "PDF browse, search and visual-page tools are exposed to MCP clients");

        var docs = Path.Combine(project, "docs");
        Directory.CreateDirectory(docs);
        var projectPdf = Path.Combine(docs, "board.pdf");
        await File.WriteAllBytesAsync(projectPdf, BuildTwoPagePdf());
        var fixtureInfo = PdfDocumentService.Inspect(projectPdf);
        check(fixtureInfo.PageCount == 2, "two-page PDF fixture parses locally");
        var external = Path.Combine(temporaryRoot, "pdf-examples");
        Directory.CreateDirectory(external);
        await File.WriteAllBytesAsync(Path.Combine(external, "reference.pdf"), BuildTwoPagePdf());

        Task<string> Call(string name, object arguments) =>
            session.CallToolAsync(name, JsonSerializer.Serialize(arguments));
        static bool Error(string text) => text.Contains("\"error\"", StringComparison.OrdinalIgnoreCase);

        var rootList = await Call("pdf_list", new { scope = "project" });
        var docsList = await Call("pdf_list", new { scope = "project", directory = "docs" });
        check(rootList.Contains("docs", StringComparison.Ordinal) &&
              docsList.Contains("docs/board.pdf", StringComparison.Ordinal),
            "pdf_list browses project documents one directory at a time");

        var info = await Call("pdf_inspect", new { scope = "project", path = "docs/board.pdf" });
        if (Error(info)) throw new Exception("Fixture PDF inspection failed: " + info);
        using (var json = JsonDocument.Parse(info))
            check(json.RootElement.GetProperty("pageCount").GetInt32() == 2 &&
                  json.RootElement.GetProperty("sha256").GetString()!.Length == 64,
                "pdf_inspect identifies page count and stable file hash");

        var search = await Call("pdf_inspect", new
        {
            scope = "project", path = "docs/board.pdf", query = "STM32F407", startPage = 1,
            maxPages = 1
        });
        using (var json = JsonDocument.Parse(search))
            check(json.RootElement.GetProperty("matches")[0].GetProperty("page").GetInt32() == 1 &&
                  json.RootElement.GetProperty("nextPage").GetInt32() == 2,
                "datasheet phrase search returns a page citation and continuation");

        var pageText = await Call("pdf_page", new
        {
            scope = "project", path = "docs/board.pdf", page = 1, maxCharacters = 100
        });
        using (var json = JsonDocument.Parse(pageText))
            check(json.RootElement.GetProperty("text").GetString()!.Contains("STM32F407",
                      StringComparison.Ordinal) &&
                  json.RootElement.GetProperty("image").ValueKind == JsonValueKind.Null,
                "pdf_page returns bounded extractable text without a requested image");

        var imageArgs = JsonSerializer.Serialize(new
        {
            scope = "project", path = "docs/board.pdf", page = 2, includeImage = true,
            x = 0.1, y = 0.1, width = 0.8, height = 0.5, maxDimension = 800
        });
        var detailed = await session.CallToolDetailedAsync("pdf_page", imageArgs);
        using (var json = JsonDocument.Parse(detailed.Text))
            check(detailed.Images.Count == 1 && detailed.Images[0].MimeType == "image/png" &&
                  detailed.Images[0].Data.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
                  json.RootElement.GetProperty("page").GetInt32() == 2 &&
                  json.RootElement.GetProperty("image").GetProperty("PixelWidth").GetInt32() > 0,
                "real MCP call preserves cropped PNG image and text blocks together");
        var legacyText = await session.CallToolAsync("pdf_page", imageArgs);
        check(legacyText.Contains("\"page\":2", StringComparison.Ordinal) &&
              !legacyText.Contains("iVBOR", StringComparison.Ordinal),
            "text-only MCP call remains compatible and does not embed base64 image data");

        var denied = await Call("pdf_page", new
        {
            scope = "external", rootId = "not-approved", path = "reference.pdf", page = 1
        });
        var escape = await Call("pdf_inspect", new
        {
            scope = "project", path = "../pdf-examples/reference.pdf"
        });
        check(Error(denied) && Error(escape),
            "PDF reads reject unapproved external roots and project path traversal");

        var priorApproval = authorizer.Allow;
        try
        {
            authorizer.Allow = true;
            var opened = await Call("external_project_open", new { directory = external });
            using var json = JsonDocument.Parse(opened);
            var rootId = json.RootElement.GetProperty("rootId").GetString()!;
            var externalInfo = await Call("pdf_inspect", new
            {
                scope = "external", rootId, path = "reference.pdf"
            });
            check(externalInfo.Contains("\"pageCount\":2", StringComparison.Ordinal),
                "approved external directory supports read-only PDF inspection");
        }
        finally { authorizer.Allow = priorApproval; }
    }

    // The fixture is generated in the test's unique temp directory and removed by Program's cleanup.
    // Page 1 has searchable text; page 2 is vector-only so image delivery is exercised.
    private static byte[] BuildTwoPagePdf()
    {
        using var output = new MemoryStream();
        var offsets = new List<long> { 0 };
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
        void Object(int number, string body)
        {
            offsets.Add(output.Position);
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }
        static string Stream(string content) =>
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream";

        Write("%PDF-1.4\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R >>");
        Object(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>");
        Object(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        Object(6, Stream("BT /F1 18 Tf 40 730 Td (STM32F407 pin PA0) Tj ET\n" +
            "0 0 1 RG 40 200 m 400 200 l S\n"));
        Object(7, Stream("0 0 1 RG 40 100 m 400 100 l S\n"));
        var xref = output.Position;
        Write($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }
}
