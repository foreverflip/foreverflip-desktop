using ForeverFlip.Desktop;
using System.Text.RegularExpressions;

internal static class ProfitTests
{
    public static void Run()
    {
        long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),day=now/86400;
        string source="ForeverFlipDB={ledgers={Synthetic={profitView={schema=1,at="+now+",days={[\""+day+"\"]={vendor={spent=100,made=150,profit=50,unknown=0},resale={spent=200,made=500,profit=0,unknown=1}}},stock={vendor={quantity=2,cost=80,unknown=0}}}}}}";
        var books=ProfitBook.Read(LuaReader.Read(Literal(source)));
        Check(books.Count==1&&books[0].Days.Count==2,"Local accounting snapshot was not read");
        var rows=ProfitBook.Rows(books,1,false,now);
        Check(rows[0].Spent==100&&rows[0].Made==150&&rows[0].Profit==50&&rows[0].Quantity==2&&rows[0].Cost==80,"Independent vendor totals differ");
        Check(rows[1].Profit==0&&rows[1].Unknown==1&&rows[1].ProfitText.Contains("known"),"Unknown AH cost/fees became realized profit");
        Check(ProfitBook.Rows(books,1,true,now).Count==2,"Daily strategy breakdown missing");
        var adjusted=ProfitBook.Read(LuaReader.Read(Literal(source.Replace("profit=50,unknown=0","profit=-20,unknown=0,manual=2"))));
        var manual=ProfitBook.Rows(adjusted,1,false,now)[0];
        Check(manual.Spent==100&&manual.Made==150&&manual.Profit==-20&&manual.Manual==2&&manual.ProfitText.EndsWith(" *"),"Manual adjustment provenance or spending changed");
        Check(ProfitBook.Rows(adjusted,1,true,now).Single(r=>r.Strategy=="Vendor").Manual==2,"Daily manual provenance missing");
        Check(ProfitBook.Read(LuaReader.Read(Literal(source.Replace("profit=50,unknown=0","profit=50,unknown=0,manual=-1"))))[0].Days.Count==1,"Invalid adjustment count accepted");
        Check(ProfitBook.Money(10001)=="1g 1c"&&ProfitBook.Money(-101)=="-1s 1c"&&ProfitBook.Money(.5m)=="~1c","Money units/sign/weighted approximation changed");
        Check(ProfitBook.Read(LuaReader.Read(Literal("ForeverFlipDB={ledgers={bad={profitView={schema=1,at=999999999999999}}}}" ))).Count==0,"Future/invalid accounting accepted");
        Check(ProfitBook.Read(LuaReader.Read(Literal("ForeverFlipDB={ledgers={}}" ))).Count==0,"Empty save acquired fake profit");
        Console.WriteLine("PASS local profit view: independent totals, UTC daily strategies, current stock, unknown AH evidence, units and empty/invalid data");
    }
    static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
    internal static string Literal(string source)
    {
        int at=source.IndexOf('=')+1;
        return source[..at]+Regex.Replace(source[at..],@"\b([A-Za-z_][A-Za-z0-9_]*)=","[\"$1\"]=");
    }
}
