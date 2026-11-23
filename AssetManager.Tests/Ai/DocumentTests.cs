using AssetManager.AI.Enrichment;
using AssetManager.AI.Runtime;
using AssetManager.Core.Domain;
using AssetManager.Core.Settings;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Settings;
using AssetManager.Tests.Fixtures;
using Microsoft.Extensions.AI;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace AssetManager.Tests.Ai;

[Collection(AiModelCollection.Name)]
public class DocumentTests
{
    private static byte[] Pdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        var y = 780;
        foreach (var line in lines)
        {
            page.AddText(line, 11, new PdfPoint(40, y), font);
            y -= 16;
        }
        return builder.Build();
    }

    private static readonly string[] RoyaltyFreeLicense =
    [
        "END USER LICENSE AGREEMENT",
        "Thank you for purchasing the Medieval Props pack.",
        "You may use these assets royalty-free in personal and commercial projects.",
        "Attribution is appreciated but not required.",
        "You may not resell or redistribute the assets as they are.",
        "Contact: https://studio.example.com/support",
    ];

    [Fact]
    public void Extracts_text_and_links_from_each_format()
    {
        using var dir = FixtureBuilder.TempDir();
        var pdf = DocumentExtractor.Extract(dir.Write("license.pdf", Pdf(RoyaltyFreeLicense)), "license.pdf");
        Assert.Null(pdf.Error);
        Assert.Contains("royalty-free in personal and commercial projects", pdf.Text);
        Assert.Contains("\n", pdf.Text); // lines stay separate
        Assert.Equal(["https://studio.example.com/support"], pdf.Urls);
        Assert.True(pdf.LooksLikeLicense);

        var html = DocumentExtractor.Extract(dir.Write("readme.html",
            "<html><head><style>.x{color:red}</style><script>var a=1;</script></head><body><h1>Forest</h1><p>Free &amp; open.</p>" +
            "<p>See <a href=\"https://forest.example.org/docs\">docs</a>.</p></body></html>"), "Docs/readme.html");
        Assert.Contains("Forest", html.Text);
        Assert.Contains("Free & open.", html.Text);
        Assert.DoesNotContain("color:red", html.Text);
        Assert.DoesNotContain("var a", html.Text);
        Assert.Equal(["https://forest.example.org/docs"], html.Urls);
        Assert.Equal("readme.html", html.Name);

        var md = DocumentExtractor.Extract(dir.Write("CREDITS.md", "# Credits\r\n\r\nMusic by Jane (https://jane.example.net).\r\n"), "CREDITS.md");
        Assert.Equal("# Credits\n\nMusic by Jane (https://jane.example.net).", md.Text);
        Assert.Equal(["https://jane.example.net"], md.Urls);

        var url = DocumentExtractor.Extract(dir.Write("Itch.io.url", "[{000214A0-0000-0000-C000-000000000046}]\r\nProp3=19,11\r\n[InternetShortcut]\r\nIDList=\r\nURL=https://someone.itch.io/forest-pack\r\n"), "Itch.io.url");
        Assert.True(url.IsShortcut);
        Assert.Equal(["https://someone.itch.io/forest-pack"], url.Urls);

        var webloc = DocumentExtractor.Extract(dir.Write("Store.webloc", "<?xml version=\"1.0\"?><plist><dict><key>URL</key><string>https://store.example.com/p?a=1&amp;b=2</string></dict></plist>"), "Store.webloc");
        Assert.Equal(["https://store.example.com/p?a=1&b=2"], webloc.Urls);

        var rtf = DocumentExtractor.Extract(dir.Write("license.rtf", @"{\rtf1\ansi{\fonttbl\f0\fswiss Helvetica;}\f0\pard Free for commercial use.\par No credit needed.}"), "license.rtf");
        Assert.Contains("Free for commercial use.", rtf.Text);
        Assert.DoesNotContain("Helvetica", rtf.Text);
        Assert.DoesNotContain("\\", rtf.Text);

        var broken = DocumentExtractor.Extract(dir.Write("broken.pdf", "%PDF-1.4 garbage"), "broken.pdf");
        Assert.NotNull(broken.Error);
        Assert.Equal("", broken.Text);
    }

    [Fact]
    public void Truncation_keeps_the_opening_and_license_passages()
    {
        var filler = string.Join("\n\n", Enumerable.Range(1, 60).Select(i => $"Chapter {i}: how to place trees and rocks in your scene, step {i}, with many details about tools."));
        var text = "Forest Pack manual.\n\n" + filler + "\n\nLICENSE: You may use this pack in commercial games. Attribution is not required.\n\n" + filler;

        var cut = DocumentExtractor.Truncate(text, 800);

        Assert.True(cut.Length <= 900, $"length {cut.Length}");
        Assert.StartsWith("Forest Pack manual.", cut);
        Assert.Contains("You may use this pack in commercial games.", cut);
        Assert.Contains("[...]", cut);
        Assert.Equal("short", DocumentExtractor.Truncate("short", 800));
    }

    [Fact]
    public void Prompt_puts_license_documents_first_within_budget()
    {
        var readme = new SourceDocument("readme.txt", new string('r', 5000), []);
        var license = new SourceDocument("Docs/LICENSE.txt", "CC0 1.0 Universal. " + new string('l', 5000), []);
        var link = new SourceDocument("Itch.io.url", "", ["https://someone.itch.io/forest"]);

        var prompt = DocumentExtractor.ToPrompt([readme, link, license], budget: 3000);

        Assert.True(prompt.IndexOf("Docs/LICENSE.txt", StringComparison.Ordinal) < prompt.IndexOf("readme.txt", StringComparison.Ordinal));
        Assert.Contains("CC0 1.0 Universal.", prompt);
        Assert.Contains("Link: https://someone.itch.io/forest", prompt);
        Assert.InRange(prompt.Length, 1000, 3400);
    }

    [Fact]
    public void Finds_documents_next_to_an_archive_source()
    {
        using var dir = FixtureBuilder.TempDir();
        var zip = dir.Write("Pack/forest.zip", [1, 2, 3]);
        dir.Write("Pack/LICENSE.txt", "CC0");
        dir.Write("Pack/Itch.io.url", "[InternetShortcut]\nURL=https://someone.itch.io/forest\n");
        dir.Write("Pack/other.png", [0]);
        dir.Write("Pack/Sub/readme.txt", "nested, not a sibling");

        var docs = DocumentExtractor.FindNear(zip, new FileClassifier(new ClassificationOptions()));

        Assert.Equal(["Itch.io.url", "LICENSE.txt"], docs.Select(d => d.RelativePath));
    }

    [Fact]
    public void Link_shortcut_is_stored_as_a_source_link_without_ai()
    {
        var docs = new[] { new SourceDocument("Itch.io.url", "", ["https://someone.itch.io/forest"]) };
        var source = new Source();

        SourceFacts.Apply(source, docs, ai: null);

        var link = Assert.Single(source.Links);
        Assert.Equal(("https://someone.itch.io/forest", "Itch.io", "Itch.io.url"), (link.Url, link.Label, link.Evidence));
        Assert.Null(source.License.Kind); // no AI answer: license stays unknown
    }

    [Fact]
    public async Task License_in_pdf_is_recorded_with_evidence()
    {
        using var dir = FixtureBuilder.TempDir();
        var docs = new[] { DocumentExtractor.Extract(dir.Write("license.pdf", Pdf(RoyaltyFreeLicense)), "license.pdf") };
        var client = new FakeChatClient("""
            {"description":"Medieval props.","license":{"kind":"Royalty-free (custom EULA)","commercial":"yes","attribution":"no","attribution_text":null,"evidence":"License.PDF"},
             "links":[{"url":"https://studio.example.com/support","label":"Support","evidence":"license.pdf"},{"url":"https://made-up.example","label":null,"evidence":"nowhere.txt"}],
             "script_notes":null,"style":[],"tags":[]}
            """);

        var result = await new StructuredChat(client).AskAsync<PackResult>(
            [new ChatMessage(ChatRole.User, "Documents:\n" + DocumentExtractor.ToPrompt(docs))], ct: TestContext.Current.CancellationToken);
        var source = new Source();
        SourceFacts.Apply(source, docs, result);

        Assert.Contains("royalty-free in personal and commercial projects", client.Requests[0].Messages[^1].Text);
        Assert.Equal(TriState.Yes, source.License.Commercial);
        Assert.Equal(TriState.No, source.License.Attribution);
        Assert.Equal("license.pdf", source.License.Evidence);
        Assert.Equal("license.pdf", source.Links[0].Evidence);
        Assert.Null(source.Links[1].Evidence); // cited a document that was not provided
        Assert.Equal("license.pdf", SourceFacts.Resolve(docs, "END USER LICENSE AGREEMENT")); // quoted instead of named
        Assert.Null(SourceFacts.Resolve(docs, "license")); // too short to be a quote
    }

    [Fact]
    public void User_edited_license_and_links_are_kept()
    {
        var source = new Source { UserEdited = EditedFields.License | EditedFields.Links, License = new LicenseInfo { Kind = "Mine" }, Links = [] };
        var ai = new PackResult { License = new LicenseResult { Kind = "CC0", Commercial = TriState.Yes }, Links = [new LinkResult { Url = "https://x.example" }] };

        SourceFacts.Apply(source, [new SourceDocument("a.url", "", ["https://y.example"])], ai);

        Assert.Equal("Mine", source.License.Kind);
        Assert.Empty(source.Links);
    }

    [AiModelFact]
    public async Task Real_model_reads_the_pdf_license()
    {
        using var dir = FixtureBuilder.TempDir();
        var docs = new[]
        {
            DocumentExtractor.Extract(dir.Write("license.pdf", Pdf(RoyaltyFreeLicense)), "license.pdf"),
            DocumentExtractor.Extract(dir.Write("Itch.io.url", "[InternetShortcut]\nURL=https://someone.itch.io/medieval\n"), "Itch.io.url"),
        };
        await using var host = new LlamaServerHost("chat");
        await host.StartAsync(new LlamaServerOptions(AiModelFactAttribute.Model!, null, AiAcceleration.Auto, false, 4096), TestContext.Current.CancellationToken);
        var client = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("x"),
            new OpenAI.OpenAIClientOptions { Endpoint = new Uri(host.Endpoint!, "v1") }).GetChatClient("local").AsIChatClient();

        var result = await new StructuredChat(client).AskAsync<PackResult>(
            [new ChatMessage(ChatRole.User, "Pack \"Medieval Props\" (40 FBX models).\n\n" + DocumentExtractor.ToPrompt(docs))],
            ct: TestContext.Current.CancellationToken);
        var source = new Source();
        SourceFacts.Apply(source, docs, result);

        var answer = System.Text.Json.JsonSerializer.Serialize(result, StructuredChat.JsonOptions);
        Assert.True(source.License.Commercial == TriState.Yes, answer);
        Assert.True(source.License.Evidence == "license.pdf", answer);
        Assert.Contains(source.Links, l => l.Url == "https://someone.itch.io/medieval");
    }
}
