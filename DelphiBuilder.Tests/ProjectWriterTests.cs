using System.Text;
using System.Xml.Linq;
using DelphiBuilder;
using Xunit;

namespace DelphiBuilder.Tests;

public sealed class ProjectWriterTests : IDisposable
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/developer/msbuild/2003";
    private static readonly string[] EditedProperties =
    [
        "VerInfo_MajorVer", "VerInfo_MinorVer", "VerInfo_Release", "VerInfo_Build",
        "VerInfo_IncludeVerInfo", "VerInfo_Keys", "DCC_ExeOutput"
    ];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DelphiBuilder.Tests", Guid.NewGuid().ToString("N"));
    private readonly ProjectService _service = new();

    public ProjectWriterTests()
    {
        Directory.CreateDirectory(_root);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlternatingPlatformsUpdatesExistingGroupsWithoutAppending(bool keyed)
    {
        var win32Condition = keyed ? "'$(Cfg_2_Win32)'!=''" : Target("Release", "Win32");
        var win64Condition = keyed ? "'$(Cfg_2_Win64)'!=''" : Target("Release", "Win64");
        var win64 = Group(win64Condition, "1.0.0.0", @"D:\JQSoft\HIS5.5\");
        var debug = Group(Target("Debug", "Win32"), "8.0.0.1", @".\debug");
        var path = Write(Envelope(Group(win32Condition, "5.5.0.122", @"D:\JQSoft\HIS5.5\") + win64 + debug));

        _service.Save(path, "Release", "Win32", "5.5.0.123", @"D:\JQSoft\HIS5.5\");
        Assert.Contains(win64, File.ReadAllText(path));
        Assert.Contains(debug, File.ReadAllText(path));
        _service.Save(path, "Release", "Win64", "1.0.0.1", @".\release64");
        _service.Save(path, "Release", "Win32", "5.5.0.124", @".\release32");

        Assert.Single(Groups(path, win32Condition));
        Assert.Single(Groups(path, win64Condition));
        Assert.Equal(4, XDocument.Load(path).Root!.Elements(Ns + "PropertyGroup").Count());
        AssertTarget(path, "Release", "Win32", "5.5.0.124", @".\release32");
        AssertTarget(path, "Release", "Win64", "1.0.0.1", @".\release64");
        Assert.Contains(debug, File.ReadAllText(path));
        var saved = File.ReadAllBytes(path);
        _service.Save(path, "Release", "Win32", "5.5.0.124", @".\release32");
        Assert.Equal(saved, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingDuplicateDefinitionsAreRemovedWithoutDeletingUnrelatedContent(bool withOtherContent)
    {
        var condition = Target("Release", "Win32");
        var extra = withOtherContent ? "    <!-- 保留用户配置 -->\n    <DCC_Define>CUSTOM;$(DCC_Define)</DCC_Define>\n" : "";
        var old = Group(condition, "5.5.0.122", @"D:\JQSoft\HIS5.5\", extra,
            attributes: withOtherContent ? " Label=\"UserSettings\"" : "");
        var win64 = Group(Target("Release", "Win64"), "1.0.0.0", @"D:\JQSoft\HIS5.5\");
        var path = Write(Envelope(old + win64 + Group(condition, "5.5.0.123", @"D:\JQSoft\HIS5.5\")));

        _service.Save(path, "Release", "Win32", "5.5.0.124", @".\updated");

        var groups = Groups(path, condition);
        Assert.Equal(withOtherContent ? 2 : 1, groups.Length);
        foreach (var property in EditedProperties)
            Assert.Single(groups.SelectMany(group => group.Elements(Ns + property)));
        AssertTarget(path, "Release", "Win32", "5.5.0.124", @".\updated");
        Assert.Contains(win64, File.ReadAllText(path));
        AssertTarget(path, "Release", "Win64", "1.0.0.0", @"D:\JQSoft\HIS5.5\");
        if (withOtherContent)
        {
            Assert.Contains(extra, File.ReadAllText(path));
            Assert.Equal("UserSettings", groups[0].Attribute("Label")?.Value);
            Assert.Equal("CUSTOM;BASE", _service.Load(path, "Release", "Win32").Properties["DCC_Define"]);
        }
        var saved = File.ReadAllBytes(path);
        _service.Save(path, "Release", "Win32", "5.5.0.124", @".\updated");
        Assert.Equal(saved, File.ReadAllBytes(path));
    }

    [Fact]
    public void LaterPropertyForOtherPlatformDoesNotForceAnOverrideGroup()
    {
        var later = """
              <PropertyGroup Condition="'$(Config)'=='Release'">
                <VerInfo_Build Condition="'$(Platform)'=='Win64'">99</VerInfo_Build>
                <DCC_ExeOutput Condition="'$(Platform)'=='Win64'">.\conditional64</DCC_ExeOutput>
              </PropertyGroup>
            """ + "\n";
        var condition = Target("Release", "Win32");
        var path = Write(Envelope(Group(condition, "5.5.0.122", @".\old32")
            + Group(Target("Release", "Win64"), "1.0.0.0", @".\old64") + later));

        _service.Save(path, "Release", "Win32", "5.5.0.123", @".\new32");

        Assert.Single(Groups(path, condition));
        AssertTarget(path, "Release", "Win32", "5.5.0.123", @".\new32");
        var win64 = _service.Load(path, "Release", "Win64");
        Assert.Equal("99", win64.Properties["VerInfo_Build"]);
        Assert.Equal(@".\conditional64", win64.OutputPath);
        Assert.Contains(later, File.ReadAllText(path));
    }

    [Fact]
    public void ActualLaterSharedOverridesStillRequireOnlyOneIsolatedTargetGroup()
    {
        var condition = Target("Release", "Win32");
        var shared = Group("", "9.0.0.1", @".\shared");
        var path = Write(Envelope(Group(condition, "5.5.0.122", @".\old32") + shared));

        _service.Save(path, "Release", "Win32", "5.5.0.123", @".\release32");
        _service.Save(path, "Release", "Win32", "5.5.0.124", @".\release32");

        Assert.Single(Groups(path, condition));
        AssertTarget(path, "Release", "Win32", "5.5.0.124", @".\release32");
        AssertTarget(path, "Release", "Win64", "9.0.0.1", @".\shared");
        AssertTarget(path, "Debug", "Win32", "9.0.0.1", @".\shared");
        Assert.Contains(shared, File.ReadAllText(path));
    }

    [Fact]
    public void EarlierDefinitionsUsedByOtherPropertiesArePreservedWithoutAddingGroups()
    {
        var condition = Target("Release", "Win32");
        var capture = """
              <PropertyGroup Condition="'$(Config)'=='Release' and '$(Platform)'=='Win32'">
                <CapturedBuild>&#36;( VerInfo_Build )</CapturedBuild>
              </PropertyGroup>
            """ + "\n";
        var path = Write(Envelope(Group(condition, "5.5.0.122", @".\old32") + capture
            + Group(condition, "5.5.0.123", @".\current32")));

        _service.Save(path, "Release", "Win32", "5.5.0.124", @".\updated32");
        _service.Save(path, "Release", "Win32", "5.5.0.125", @".\updated32");

        Assert.Equal(3, Groups(path, condition).Length);
        Assert.Equal("122", _service.Load(path, "Release", "Win32").Properties["CapturedBuild"]);
        AssertTarget(path, "Release", "Win32", "5.5.0.125", @".\updated32");
        Assert.Contains(capture, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InPlaceUpdatesPreserveOriginalEncodingNamespaceAndUntouchedTarget(bool legacyEncoding)
    {
        var encoding = legacyEncoding ? Encoding.GetEncoding(936) : new UTF8Encoding(true);
        var win64 = Group(Target("Release", "Win64"), "1.0.0.0", @".\旧目录64", prefix: "p:").ReplaceLineEndings("\r\n");
        var source = Envelope(Group(Target("Release", "Win32"), "5.5.0.122", @".\旧目录32", prefix: "p:") + win64,
            legacyEncoding ? "gb2312" : "utf-8", "p:").ReplaceLineEndings("\r\n");
        var path = Write(source, encoding);

        _service.Save(path, "Release", "Win32", "5.5.0.123", @"D:\JQSoft\HIS5.5\新目录");

        var bytes = File.ReadAllBytes(path);
        if (!legacyEncoding) Assert.True(bytes.AsSpan().StartsWith(encoding.GetPreamble()));
        var saved = encoding.GetString(bytes);
        Assert.Contains(win64, saved);
        Assert.DoesNotContain("\n", saved.Replace("\r\n", "", StringComparison.Ordinal));
        var group = Assert.Single(Groups(path, Target("Release", "Win32")));
        Assert.Equal("p", group.GetPrefixOfNamespace(Ns));
        AssertTarget(path, "Release", "Win32", "5.5.0.123", @"D:\JQSoft\HIS5.5\新目录");
        AssertTarget(path, "Release", "Win64", "1.0.0.0", @".\旧目录64");
    }

    private void AssertTarget(string path, string configuration, string platform, string version, string output)
    {
        var evaluation = _service.Load(path, configuration, platform);
        Assert.Equal(version, evaluation.Version);
        Assert.Equal(output, evaluation.OutputPath);
        Assert.Equal(Keys(version), evaluation.Properties["VerInfo_Keys"]);
    }

    private static XElement[] Groups(string path, string condition) => XDocument.Load(path).Root!
        .Elements(Ns + "PropertyGroup").Where(group => group.Attribute("Condition")?.Value == condition).ToArray();

    private static string Target(string configuration, string platform) => $"'$(Config)'=='{configuration}' and '$(Platform)'=='{platform}'";

    private static string Keys(string version) => "CompanyName=合肥晶奇亨源合义信息科技有限责任公司;FileDescription=体检系统基础信息;"
        + $"FileVersion={version};InternalName=;LegalCopyright=;LegalTrademarks=;OriginalFilename=;ProductName=医院信息管理平台;ProductVersion={version};"
        + "Comments=;HyInParam=1：单位档案维护|2：团检预约|3：登记台|4：体检医生工作站|5：总检工作站|6：体检报告|7：安徽省职业病上传|8：体检收入查询;HyBanInfo=";

    private static string Group(string condition, string version, string output, string extra = "", string prefix = "", string attributes = "")
    {
        var parts = version.Split('.');
        return $"""
              <{prefix}PropertyGroup Condition="{condition}"{attributes}>
                <{prefix}VerInfo_MajorVer>{parts[0]}</{prefix}VerInfo_MajorVer>
                <{prefix}VerInfo_MinorVer>{parts[1]}</{prefix}VerInfo_MinorVer>
                <{prefix}VerInfo_Release>{parts[2]}</{prefix}VerInfo_Release>
                <{prefix}VerInfo_Build>{parts[3]}</{prefix}VerInfo_Build>
                <{prefix}VerInfo_IncludeVerInfo>true</{prefix}VerInfo_IncludeVerInfo>
                <{prefix}VerInfo_Keys>{Keys(version)}</{prefix}VerInfo_Keys>
                <{prefix}DCC_ExeOutput>{output}</{prefix}DCC_ExeOutput>
            {extra}  </{prefix}PropertyGroup>
            """ + "\n";
    }

    private static string Envelope(string groups, string encoding = "utf-8", string prefix = "") => $"""
        <?xml version="1.0" encoding="{encoding}"?>
        <{prefix}Project {(prefix.Length == 0 ? "xmlns" : "xmlns:p")}="{Ns}">
          <{prefix}PropertyGroup>
            <{prefix}MainSource>Sample.dpr</{prefix}MainSource>
            <{prefix}DCC_Define>BASE</{prefix}DCC_Define>
          </{prefix}PropertyGroup>
        {groups}  <{prefix}ItemGroup>
            <{prefix}BuildConfiguration Include="Base"><{prefix}Key>Base</{prefix}Key></{prefix}BuildConfiguration>
            <{prefix}BuildConfiguration Include="Debug"><{prefix}Key>Cfg_1</{prefix}Key><{prefix}CfgParent>Base</{prefix}CfgParent></{prefix}BuildConfiguration>
            <{prefix}BuildConfiguration Include="Release"><{prefix}Key>Cfg_2</{prefix}Key><{prefix}CfgParent>Base</{prefix}CfgParent></{prefix}BuildConfiguration>
          </{prefix}ItemGroup>
          <{prefix}Import Project="$(BDS)\Bin\CodeGear.Delphi.Targets" />
        </{prefix}Project>
        """;

    private string Write(string text, Encoding? encoding = null)
    {
        var path = Path.Combine(_root, "Sample.dproj");
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_root, "Sample.dpr"), "program Sample; begin end.");
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
