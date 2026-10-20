using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ForeverFlip.Desktop;

// Literal-only SavedVariables reader. It never loads/evaluates Lua code.
public sealed class LuaReader(string text)
{
    int pos, nodes;
    static readonly Regex Numeric=new(@"\G[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?",RegexOptions.Compiled|RegexOptions.CultureInvariant);
    public static Dictionary<string,object?> Read(string text)
    {
        if(Encoding.UTF8.GetByteCount(text)>64*1024*1024) throw new InvalidDataException("Saved database exceeds64MiB.");
        string literal=text.TrimStart('\uFEFF');var r=new LuaReader(literal);r.Space();
        if(!r.Take("ForeverFlipDB"))throw new InvalidDataException("No ForeverFlip database found.");
        r.Need("=");var database=r.Value() as Dictionary<string,object?> ?? throw new InvalidDataException("Invalid database.");
        r.Take(";");r.Space();
        // WoW stores this addon's optional manual-export global in the same
        // file. Validate its literal syntax, but never return/upload it.
        if(r.Take("ForeverFlipExport")){
            r.Need("=");r.Space();if(r.pos<literal.Length&&(literal[r.pos]=='"'||literal[r.pos]=='\''))r.String();else r.Need("nil");
            r.Take(";");r.Space();
        }
        if(r.pos!=literal.Length)throw new InvalidDataException("Unexpected text after the saved database.");return database;
    }
    void Space()
    {
        while(pos<text.Length){if(char.IsWhiteSpace(text[pos])){pos++;continue;}
            if(pos+1<text.Length && text[pos]=='-' && text[pos+1]=='-'){while(pos<text.Length && text[pos]!='\n')pos++;continue;}break;}
    }
    bool Take(string token){Space();if(!text.AsSpan(pos).StartsWith(token,StringComparison.Ordinal))return false;pos+=token.Length;return true;}
    void Need(string token){if(!Take(token))throw new InvalidDataException("Invalid saved literal at offset "+pos);}
    object? Value(int depth=0)
    {
        if(++nodes>4_000_000 || depth>64)throw new InvalidDataException("Saved database exceeds structural limits.");
        Space();if(pos>=text.Length)throw new InvalidDataException("Incomplete game save.");
        if(text[pos]=='"'||text[pos]=='\'')return String();
        if(Take("{")){
            var result=new Dictionary<string,object?>();int index=1;
            while(!Take("}")){
                string key;
                if(Take("[")){var k=Value(depth+1);if(k is not string && k is not double)throw new InvalidDataException("Invalid table key.");key=Convert.ToString(k,CultureInfo.InvariantCulture)!;Need("]");Need("=");}
                else key=(index++).ToString(CultureInfo.InvariantCulture);
                if(!result.TryAdd(key,Value(depth+1)))throw new InvalidDataException("Duplicate table key.");
                if(Take(",")||Take(";"))continue;Need("}");break;
            }return result;
        }
        if(Take("true"))return true;if(Take("false"))return false;if(Take("nil"))return null;
        var m=Numeric.Match(text,pos);
        if(!m.Success)throw new InvalidDataException("Executable or unsupported saved text.");
        pos+=m.Length;double value=double.Parse(m.Value,CultureInfo.InvariantCulture);
        if(!double.IsFinite(value))throw new InvalidDataException("Non-finite number.");return value;
    }
    string String()
    {
        char quote=text[pos++];var b=new StringBuilder();using var bytes=new MemoryStream();
        void Flush(){if(b.Length==0)return;bytes.Write(Encoding.UTF8.GetBytes(b.ToString()));b.Clear();}
        while(pos<text.Length){char c=text[pos++];if(c==quote){Flush();return new UTF8Encoding(false,true).GetString(bytes.ToArray());}if(c!='\\'){b.Append(c);continue;}
            if(pos>=text.Length)break;c=text[pos++];
            if(c>='0'&&c<='9'){int n=c-'0',count=1;while(count<3&&pos<text.Length&&text[pos]>='0'&&text[pos]<='9'){n=n*10+text[pos++]-'0';count++;}if(n>255)throw new InvalidDataException("Invalid byte escape.");Flush();bytes.WriteByte((byte)n);}
            else b.Append(c switch{'n'=>'\n','r'=>'\r','t'=>'\t','b'=>'\b','f'=>'\f','v'=>'\v','a'=>'\a','\\'=>'\\','"'=>'"','\''=>'\'','\n'=>'\n',_=>throw new InvalidDataException("Unsupported escape.")});
        }throw new InvalidDataException("Incomplete saved string.");
    }
}
