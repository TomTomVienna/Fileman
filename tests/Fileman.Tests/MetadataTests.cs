using System.IO.Compression;
using System.Text;
using Fileman.Core.Metadata;

namespace Fileman.Tests;

public class MetadataTests
{
    [Fact]
    public void ReadsOfficeCoreProperties()
    {
        using var docx = new MemoryStream();
        using (var zip = new ZipArchive(docx, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("docProps/core.xml").Open()))
                writer.Write("""
                    <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
                        xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/">
                      <dc:title>Jahresabschluss 2025</dc:title>
                      <dc:creator>Maria Huber</dc:creator>
                      <cp:lastModifiedBy>Thomas K.</cp:lastModifiedBy>
                      <cp:revision>7</cp:revision>
                      <dcterms:modified>2026-10-12T12:30:00Z</dcterms:modified>
                    </cp:coreProperties>
                    """);
            using (var writer = new StreamWriter(zip.CreateEntry("docProps/app.xml").Open()))
                writer.Write("""<Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"><Application>Microsoft Excel</Application></Properties>""");
        }
        docx.Position = 0;

        var props = DocumentMetadataReader.Read(docx, "Bilanz.xlsx").ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal("Jahresabschluss 2025", props["Titel"]);
        Assert.Equal("Maria Huber", props["Autor"]);
        Assert.Equal("Thomas K.", props["Zuletzt gespeichert von"]);
        Assert.Equal("7", props["Revision"]);
        Assert.Equal("Microsoft Excel", props["Anwendung"]);
        Assert.True(props.ContainsKey("Geändert"));
    }

    [Fact]
    public void ReadsPdfInfoDictionary()
    {
        var pdf = "%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n" +
                  "2 0 obj << /Title (Steuererkl\\344rung 2025) /Author (Kanzlei Huber) /ModDate (D:20261012143200+02'00') >> endobj\n%%EOF";
        using var stream = new MemoryStream(Encoding.Latin1.GetBytes(pdf));

        var props = DocumentMetadataReader.Read(stream, "Erklärung.pdf").ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal("Steuererklärung 2025", props["Titel"]);
        Assert.Equal("Kanzlei Huber", props["Autor"]);
        Assert.Equal("12.10.2026 14:32", props["Geändert"]);
        Assert.Equal("1", props["Seiten"]);
    }

    [Fact]
    public void BrokenOfficeFile_GivesHintInsteadOfError()
    {
        using var stream = new MemoryStream([1, 2, 3, 4]);
        var props = DocumentMetadataReader.Read(stream, "kaputt.docx");
        Assert.Equal("Hinweis", Assert.Single(props).Key);
    }

    [Fact]
    public void UnknownFormat_HasNoEmbeddedProperties()
    {
        using var stream = new MemoryStream("text"u8.ToArray());
        Assert.Empty(DocumentMetadataReader.Read(stream, "notiz.txt"));
    }
}
