using System.Text;
using System.Text.Json;
using GalactiLog.Core.Catalogs;
using Xunit;

namespace GalactiLog.Core.Tests.Catalogs;

public class StaticCatalogLoaderTests
{
    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void ParseOpenNgc_NormalizesNameAndBuildsMessierField()
    {
        const string csv =
            "Name;Type;RA;Dec;Const;MajAx;MinAx;PosAng;B-Mag;V-Mag;SurfBr;Common names;M\n" +
            "NGC0031;*;00:00:00.0;+40:00:00;And;;;;;;;;\n" +
            "NGC0224;G;00:42:44.3;+41:16:09;And;;;;;;;;031\n" +
            ";G;00:00:00.0;+00:00:00;And;;;;;;;;\n";

        var rows = StaticCatalogLoader.ParseOpenNgc(Stream(csv));

        Assert.Equal(2, rows.Count);
        Assert.Equal("NGC 31", rows[0].Name);
        Assert.Null(rows[0].Messier);
        Assert.Equal("NGC 224", rows[1].Name);
        Assert.Equal("M 031", rows[1].Messier);
    }

    // Review fix, item 6: every reader of openngc_catalog.messier keys on the zero-padded
    // three-digit form, so the padding is applied at load rather than assumed from the file.
    // The bundled openngc.csv already pads its M column; a future catalog revision that does
    // not must still load to the same stored form.
    [Fact]
    public void ParseOpenNgc_PadsUnpaddedMessierNumberToThreeDigits()
    {
        const string csv =
            "Name;Type;RA;Dec;Const;MajAx;MinAx;PosAng;B-Mag;V-Mag;SurfBr;Common names;M\n" +
            "NGC0224;G;00:42:44.3;+41:16:09;And;;;;;;;;31\n" +
            "NGC6720;PN;18:53:35.0;+33:01:42;Lyr;;;;;;;;7\n";

        var rows = StaticCatalogLoader.ParseOpenNgc(Stream(csv));

        Assert.Equal("M 031", rows[0].Messier);
        Assert.Equal("M 007", rows[1].Messier);
    }

    [Fact]
    public void ParseOpenNgc_ParsesRaAndDec()
    {
        const string csv =
            "Name;Type;RA;Dec;Const;MajAx;MinAx;PosAng;B-Mag;V-Mag;SurfBr;Common names;M\n" +
            "NGC0001;G;00:07:15.84;+27:42:29.1;Peg;;;;;;;;\n" +
            "NGC0002;G;bad;bad;Peg;;;;;;;;\n" +
            "NGC0003;G;00:07;+27:42;Peg;;;;;;;;\n";

        var rows = StaticCatalogLoader.ParseOpenNgc(Stream(csv));

        Assert.Equal((0 + 7.0 / 60 + 15.84 / 3600) * 15, rows[0].Ra);
        Assert.Equal(27 + 42.0 / 60 + 29.1 / 3600, rows[0].Dec);

        // Malformed values (non-numeric part, wrong part count) yield null.
        Assert.Null(rows[1].Ra);
        Assert.Null(rows[1].Dec);
        Assert.Null(rows[2].Ra); // only 2 parts
        Assert.Null(rows[2].Dec); // only 2 parts
    }

    [Fact]
    public void ParseCaldwell_SkipsBlankKeyAndMapsPayload()
    {
        const string csv =
            "catalog_id,ngc_ic_id,object_type,constellation,common_name\n" +
            "C1,NGC 188,Open Cluster,Cep,\n" +
            ",NGC 999,Open Cluster,Cep,Skipped\n";

        var rows = StaticCatalogLoader.ParseCaldwell(Stream(csv));

        Assert.Single(rows);
        Assert.Equal("caldwell", rows[0].CatalogName);
        Assert.Equal("C1", rows[0].CatalogNumber);
        // Raw, not NGC-normalized (spec 9.8 normalizes at match time, not load time).
        Assert.Equal("NGC 188", rows[0].NgcName);

        using var doc = JsonDocument.Parse(rows[0].Payload);
        Assert.Equal("Open Cluster", doc.RootElement.GetProperty("object_type").GetString());
        Assert.Equal("Cep", doc.RootElement.GetProperty("constellation").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("common_name").ValueKind);
    }

    [Fact]
    public void ParseAbell_NgcNameAlwaysNull()
    {
        const string csv =
            "abell_id,ra,dec,richness_class,distance_class,bm_type,redshift\n" +
            "Abell 1,10.5,-20.1,2,4,I,0.05\n";

        var rows = StaticCatalogLoader.ParseAbell(Stream(csv));

        Assert.Single(rows);
        Assert.Null(rows[0].NgcName);

        using var doc = JsonDocument.Parse(rows[0].Payload);
        Assert.Equal(10.5, doc.RootElement.GetProperty("ra").GetDouble());
        Assert.Equal(-20.1, doc.RootElement.GetProperty("dec").GetDouble());
        Assert.Equal(2, doc.RootElement.GetProperty("richness_class").GetInt32());
        Assert.Equal(4, doc.RootElement.GetProperty("distance_class").GetInt32());
        Assert.Equal("I", doc.RootElement.GetProperty("bm_type").GetString());
        Assert.Equal(0.05, doc.RootElement.GetProperty("redshift").GetDouble());
    }

    [Fact]
    public void ParseArp_TakesFirstNgcIdOnCommaList()
    {
        const string csv =
            "arp_id,ngc_ic_ids,peculiarity_class,peculiarity_description\n" +
            "Arp 1,\"NGC 1, NGC 2\",I,Spiral with low surface brightness\n";

        var rows = StaticCatalogLoader.ParseArp(Stream(csv));

        Assert.Single(rows);
        Assert.Equal("NGC 1", rows[0].NgcName);

        using var doc = JsonDocument.Parse(rows[0].Payload);
        Assert.Equal("NGC 1, NGC 2", doc.RootElement.GetProperty("ngc_ic_ids").GetString());
    }

    [Fact]
    public void ParseSac_NormalizesObjectForBothCatalogNumberAndNgcName()
    {
        const string csv =
            "Object,Other,Type,Con,RA,Dec,Mag,SBrightness,Size,Notes\n" +
            "M001,NGC 1952,SNR,Tau,05 34.5,+22 01,8.4,,6.0x4.0,Crab Nebula\n";

        var rows = StaticCatalogLoader.ParseSac(Stream(csv));

        Assert.Single(rows);
        Assert.Equal("M 1", rows[0].CatalogNumber);
        Assert.Equal(rows[0].CatalogNumber, rows[0].NgcName);

        using var doc = JsonDocument.Parse(rows[0].Payload);
        Assert.Equal("05 34.5", doc.RootElement.GetProperty("ra").GetString());
        Assert.Equal("+22 01", doc.RootElement.GetProperty("dec").GetString());
        Assert.Equal("6.0x4.0", doc.RootElement.GetProperty("size").GetString());
    }

    [Fact]
    public void ParseHerschel400_UsesNgcIdForBothFields()
    {
        const string csv =
            "ngc_id,object_type,constellation,magnitude\n" +
            "NGC 40,PN,Cep,11.4\n";

        var rows = StaticCatalogLoader.ParseHerschel400(Stream(csv));

        Assert.Single(rows);
        Assert.Equal("herschel400", rows[0].CatalogName);
        Assert.Equal("NGC 40", rows[0].CatalogNumber);
        Assert.Equal("NGC 40", rows[0].NgcName);
    }
}
