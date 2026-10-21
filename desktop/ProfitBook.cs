using System.Globalization;

namespace ForeverFlip.Desktop;

// Local-only addon aggregates. Never passed to the market upload serializer.
public sealed record ProfitDay(long Day,string Strategy,decimal Spent,decimal Made,decimal Profit,long Unknown,long Manual=0);
public sealed record ProfitStock(string Strategy,decimal Quantity,decimal Cost,decimal Unknown,decimal WipCost=0,decimal Unresolved=0,decimal ArchivedCost=0);
public sealed record ProfitBook(string Character,long At,IReadOnlyList<ProfitDay> Days,IReadOnlyList<ProfitStock> Stock,bool LegacyDates=false,bool StockIncomplete=false)
{
    public static readonly string[] Strategies=["vendor","resale","error","crafting","other"];
    static Dictionary<string,object?> Map(object? value)=>value as Dictionary<string,object?>??[];
    static decimal? Number(object? v)=>v is double n&&double.IsFinite(n)&&Math.Abs(n)<=1e14?(decimal)n:null;
    public static IReadOnlyList<ProfitBook> Read(Dictionary<string,object?> db)
    {
        var books=new List<ProfitBook>();long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach(var (character,raw) in Map(db.GetValueOrDefault("ledgers")).Take(200)){
            var v=Map(Map(raw).GetValueOrDefault("profitView"));
            if(Number(v.GetValueOrDefault("schema"))!=1||Number(v.GetValueOrDefault("at")) is not decimal at||at<1||at>now+300)continue;
            var days=new List<ProfitDay>();var stock=new List<ProfitStock>();
            foreach(var (key,groups) in Map(v.GetValueOrDefault("days")).Take(180)){
                if(!long.TryParse(key,NumberStyles.None,CultureInfo.InvariantCulture,out long day)||day<now/86400-179||day>now/86400)continue;
                foreach(string strategy in Strategies){
                    var r=Map(Map(groups).GetValueOrDefault(strategy));if(r.Count==0)continue;
                    var spent=Number(r.GetValueOrDefault("spent"));var made=Number(r.GetValueOrDefault("made"));var profit=Number(r.GetValueOrDefault("profit"));var unknown=Number(r.GetValueOrDefault("unknown"));
                    if(spent is null||made is null||profit is null||unknown is null||spent<0||made<0||unknown<0||unknown>1000000||unknown!=decimal.Truncate(unknown.Value))continue;
                    var manual=Number(r.GetValueOrDefault("manual"))??0;
                    if(manual<0||manual>1000000||manual!=decimal.Truncate(manual))continue;
                    days.Add(new(day,strategy,spent.Value,made.Value,profit.Value,(long)unknown.Value,(long)manual));
                }
            }
            foreach(string strategy in Strategies){
                var r=Map(Map(v.GetValueOrDefault("stock")).GetValueOrDefault(strategy));
                var q=Number(r.GetValueOrDefault("quantity"));var cost=Number(r.GetValueOrDefault("cost"));var unknown=Number(r.GetValueOrDefault("unknown"));
                var wip=Number(r.GetValueOrDefault("wipCost"))??0;
                var unresolved=Number(r.GetValueOrDefault("unresolved"))??0;
                var archived=Number(r.GetValueOrDefault("archivedCost"))??0;
                if(q>=0&&cost>=0&&unknown>=0&&wip>=0&&unresolved>=0&&archived>=0)stock.Add(new(strategy,q.Value,cost.Value,unknown.Value,wip,unresolved,archived));
            }
            books.Add(new(character,(long)at,days,stock,v.GetValueOrDefault("legacyDates") is true,v.GetValueOrDefault("stockIncomplete") is true));
        }
        return books;
    }
    public static string Money(decimal copper)
    {
        string sign=copper<0?"-":"";decimal amount=decimal.Floor(Math.Abs(copper)+.5m);
        decimal gold=decimal.Floor(amount/10000),silver=decimal.Floor(amount%10000/100),coins=amount%100;
        var parts=new List<string>();if(gold>0)parts.Add($"{gold:N0}g");if(silver>0)parts.Add($"{silver:0}s");if(coins>0||parts.Count==0)parts.Add($"{coins:0}c");string text=string.Join(" ",parts);
        return (copper!=decimal.Truncate(copper)?"~":"")+sign+text;
    }
    public static string Name(string s)=>s switch{"vendor"=>"Vendor","resale"=>"Resale","error"=>"Price errors","crafting"=>"Crafting",_=>"Other / mixed"};
    public static IReadOnlyList<ProfitDisplayRow> Rows(IEnumerable<ProfitBook> books,int days,bool daily,long? utcNow=null)
    {
        long today=(utcNow??DateTimeOffset.UtcNow.ToUnixTimeSeconds())/86400;
        var list=books.ToArray();var records=list.SelectMany(b=>b.Days).Where(r=>r.Day>=today-days+1&&r.Day<=today);
        var rows=new List<ProfitDisplayRow>();
        if(daily){
            foreach(var group in records.GroupBy(r=>(r.Day,r.Strategy)).OrderByDescending(g=>g.Key.Day).ThenBy(g=>g.Key.Strategy))
                rows.Add(new(DateTimeOffset.FromUnixTimeSeconds(group.Key.Day*86400).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),Name(group.Key.Strategy),group.Sum(r=>r.Spent),group.Sum(r=>r.Made),group.Sum(r=>r.Profit),group.Sum(r=>r.Unknown),0,0,0,Manual:group.Sum(r=>r.Manual)));
        }else foreach(string strategy in Strategies){
            var r=records.Where(x=>x.Strategy==strategy).ToArray();var stock=list.SelectMany(b=>b.Stock).Where(x=>x.Strategy==strategy).ToArray();
            rows.Add(new("",Name(strategy),r.Sum(x=>x.Spent),r.Sum(x=>x.Made),r.Sum(x=>x.Profit),r.Sum(x=>x.Unknown),stock.Sum(x=>x.Quantity),stock.Sum(x=>x.Cost),stock.Sum(x=>x.Unknown),stock.Sum(x=>x.WipCost),stock.Sum(x=>x.Unresolved),r.Sum(x=>x.Manual),stock.Sum(x=>x.ArchivedCost)));
        }
        return rows;
    }
}
public sealed record ProfitDisplayRow(string Day,string Strategy,decimal Spent,decimal Made,decimal Profit,long Unknown,decimal Quantity,decimal Cost,decimal StockUnknown,decimal WipCost=0,decimal Unresolved=0,long Manual=0,decimal ArchivedCost=0)
{
    public string SpentText=>ProfitBook.Money(Spent);
    public string MadeText=>ProfitBook.Money(Made);
    public string ProfitText=>ProfitBook.Money(Profit)+(Unknown>0?" known":"")+(Manual>0?" *":"");
    public string StockText=>$"{Quantity:0.##} / {ProfitBook.Money(Cost)}"+(WipCost>0?$" + WIP {ProfitBook.Money(WipCost)}":"")+(ArchivedCost>0?$"; ended batches: {ProfitBook.Money(ArchivedCost)} awaiting allocation":"")+(StockUnknown>0?$"; {StockUnknown:0.##} unpriced":"")+(Unresolved>0?$"; {Unresolved:0.##} unresolved":"");
}
