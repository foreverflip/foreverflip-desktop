using System.IO;

static class TestPaths
{
    public static readonly string Root=Create();
    static string Create(){string root=Path.Combine(Path.GetTempPath(),"ForeverFlip-tests");Directory.CreateDirectory(root);return root;}
}
