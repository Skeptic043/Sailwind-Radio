using System;
using System.IO;
using System.Linq;
using SailwindRadio.Models;
using SailwindRadio;
using SailwindRadio.Physical;

internal static class Program
{
    private static int checks;
    private static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    private static void Main()
    {
        using var stream=typeof(DeviceModel).Assembly.GetManifestResourceStream("SailwindRadio.Models.devices.json");
        var document=DeviceModel.Read(stream);
        Check(document.models.Length==4,"embedded original models parse through production loader");
        foreach(var model in document.models)
        {
            var names=model.parts.Select(p=>p.name).ToArray();
            Check(model.parts.Sum(p=>p.triangles.Length/3)<=new[]{3000,1000,1800,2000}[model.kind],"device stays within authored triangle budget");
            Check(names.Contains("screen")== (model.kind==0),"only radio has an independently illuminated screen");
            Check(names.Contains("control_power")&&names.Contains("icon_power"),"every device has visible power control");
            Check(names.Contains("control_master")== (model.kind==0)&&names.Contains("control_local")== (model.kind==0),"radio alone has master and local knobs");
            Check(names.Contains("control_bass")== (model.kind==3)&&names.Contains("control_volume")== (model.kind==1||model.kind==2),"woofer has bass only while speakers have local volume");
            Check(model.parts.Where(p=>p.name=="body").Sum(p=>p.vertices.Length/3)<65535,"root mesh stays within Unity16bit index capacity");
            foreach(var part in model.parts)
            {
                Check(part.vertices.Length>0&&part.triangles.Length>0,"every visible part has geometry");
                Check(part.material>=0&&part.material<document.materials.Length,"every mesh resolves an authored material");
                Check(part.pivot.Length==3&&part.pivot.All(float.IsFinite),"all mesh pivots are explicit finite local coordinates");
                if(part.name.StartsWith("indicator_"))
                {
                    var control=model.parts.Single(p=>p.name=="control_"+part.name.Substring(10));
                    Check(part.pivot.SequenceEqual(control.pivot),"knob and indicator share the same rotation pivot");
                    float minX=control.vertices.Where((_,i)=>i%3==0).Min(),maxX=control.vertices.Where((_,i)=>i%3==0).Max();
                    float minY=control.vertices.Where((_,i)=>i%3==1).Min(),maxY=control.vertices.Where((_,i)=>i%3==1).Max();
                    Check(Math.Abs((minX+maxX)/2-control.pivot[0])<.00001&&Math.Abs((minY+maxY)/2-control.pivot[1])<.00001,"rotation pivot sits at knob face center, not whole device origin");
                    Check(!DeviceModel.HasOwnLightMaterial(control.name),"level knobs do not share button light state");
                }
                if(model.kind==1)Check(part.vertices.Where((_,i)=>i%3==2).Max()<=.000001f,"satellite never extends behind its native wall contact plane");
                if(part.name.StartsWith("control_"))
                {
                    float front=model.kind==1?-.12f:model.kind==2?-.2f:model.kind==3?-.375f:-.1f;
                    Check(part.vertices.Where((_,i)=>i%3==2).Min()<front,"interactive control protrudes beyond native body collider");
                }
            }
        }
        using var wrongVersion=new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"version\":99,\"models\":[],\"materials\":[]}"));
        bool rejected=false;try{DeviceModel.Read(wrongVersion);}catch(InvalidDataException){rejected=true;}
        Check(rejected,"unsupported asset version fails before Unity object mutation");
        Check(DeviceControlVisuals.KnobAngle(0)==135&&DeviceControlVisuals.KnobAngle(.5f)==0&&DeviceControlVisuals.KnobAngle(1)==-135,"visible knob sweep follows low, middle and high level");
        Check(DeviceControlVisuals.KnobAngle(-1)==135&&DeviceControlVisuals.KnobAngle(2)==-135&&DeviceControlVisuals.KnobAngle(float.NaN)==135,"visual rotation clamps invalid levels safely");
        foreach(string key in new[]{"power","playpause","shuffle","previous","next","collections"})
        {
            Check(!DeviceModel.HasOwnLightMaterial("control_"+key),"button face stays nonemissive even while its icon is on");
            Check(DeviceModel.HasOwnLightMaterial("icon_"+key),"button glyph requires an independently owned light material");
        }
        Check(!DeviceModel.HasOwnLightMaterial("body")&&!DeviceModel.HasOwnLightMaterial("icon_master"),"cabinet and fixed knob legends never participate in button lighting");
        var state=new RadioState{Powered=true,Shuffle=true};
        Check(DeviceControlVisuals.IsLit(state,"shuffle",false)&&DeviceControlVisuals.IsLit(state,"power",false),"powered shuffle and power states light their controls");
        state.Powered=false;
        Check(!DeviceControlVisuals.IsLit(state,"shuffle",false)&&!DeviceControlVisuals.IsLit(state,"power",false),"power off extinguishes retained shuffle selection");
        state.Kind=1;state.SpeakerEnabled=true;
        Check(DeviceControlVisuals.IsLit(state,"power",false),"satellite icon lights from its own enabled state");
        state.SpeakerEnabled=false;
        Check(!DeviceControlVisuals.IsLit(state,"power",false),"satellite off extinguishes icon");
        Check(DotMatrixFont.Normalize("Café déjà vu — été…")=="CAFE DEJA VU - ETE...","original bitmap alphabet normalizes accents and punctuation");
        Check(DotMatrixFont.CanRender("Across the Blue 123")&&!DotMatrixFont.CanRender("海辺の音楽"),"unsupported scripts explicitly select the unchanged-font fallback");
        Check(!DotMatrixFont.CanRender("\ud83c"),"truncated surrogate cannot throw while evaluating metadata");
        var blank=DotMatrixFont.Rasterize("","","");
        Check(blank.Length==DotMatrixFont.Width*DotMatrixFont.Height&&blank.All(p=>p==0),"powered-off bitmap is fully transparent");
        DotMatrixFont.RasterizeInto(blank,"RADIO","","");
        Check(blank.Any(p=>p>0),"runtime paints into its reusable bitmap buffer");
        DotMatrixFont.RasterizeInto(blank,"","","");
        Check(blank.All(p=>p==0),"reusing a bitmap clears stale glyphs rather than leaving previous metadata");
        var bitmap=DotMatrixFont.Rasterize("ACROSS THE BLUE","THE TRADE WINDS","EVENING PASSAGE");
        Check(bitmap.Any(p=>p==255)&&bitmap.Any(p=>p==210),"title and metadata use distinct restrained glyph intensities");
        Check(DotMatrixFont.Rasterize("海辺の音楽","","").All(p=>p==0),"fallback lines do not also draw replacement-question-mark bitmap text");
        var marquee=new MetadataMarquee();
        marquee.Set(true,"ABCDEFGHIJKLMNOPQRSTUVWX","SHORT ARTIST","ALBUM",100);
        Check(marquee.Advance(100)&&marquee[0]=="ABCDEFGHIJKLMNOPQRST","marquee first frame is a readable twenty-character window");
        Check(!marquee.Advance(101.99)&&marquee[0]=="ABCDEFGHIJKLMNOPQRST","initial two-second dwell causes no bitmap rebuild");
        Check(marquee.Advance(102.35)&&marquee[0]=="BCDEFGHIJKLMNOPQRSTU"&&marquee[1]=="SHORT ARTIST","long title scrolls one character without moving short metadata");
        Check(!marquee.Advance(102.4),"ordinary intermediate frames do not allocate or upload another bitmap");
        Check(marquee.Advance(103.4)&&marquee[0]=="EFGHIJKLMNOPQRSTUVWX","marquee reaches final character without discarding suffix");
        Check(!marquee.Advance(105.39),"final window dwells for two seconds");
        Check(marquee.Advance(105.4)&&marquee[0]=="ABCDEFGHIJKLMNOPQRST","marquee returns to first window after end dwell");
        marquee.Set(false,"ABCDEFGHIJKLMNOPQRSTUVWX","SHORT ARTIST","ALBUM",106);
        Check(marquee.Advance(106)&&marquee[0]==""&&marquee[1]=="","power off blanks every metadata line");
        Check(!marquee.Advance(10000),"off display never uploads periodic empty frames");
        marquee.Set(true,"ABCDEFGHIJKLMNOPQRSTUVWX","SHORT ARTIST","ALBUM",10000);
        Check(marquee.Advance(10000)&&marquee[0]=="ABCDEFGHIJKLMNOPQRST"&&!marquee.Advance(10001),"power on restarts readable initial dwell");
        marquee.Set(true,"NEW TITLE","SHORT ARTIST","ALBUM",10002);
        Check(marquee.Advance(10002)&&marquee[0]=="NEW TITLE","track change immediately resets only changed line");
        marquee.Set(true,"1234567890123456789😀Z","SHORT ARTIST","ALBUM",11000);
        marquee.Advance(11000);
        Check(marquee[0].EndsWith("😀"),"viewport boundary keeps a surrogate pair intact");
        for(int line=0;line<3;line++)
        {
            string[] text={"","",""};text[line]=new string('W',34);
            var raster=DotMatrixFont.Rasterize(text[0],text[1],text[2]);
            Check(raster.Any(p=>p>0),"long metadata still produces fitted dot glyphs");
            Check(!raster.Where((p,i)=>i%DotMatrixFont.Width<16||i%DotMatrixFont.Width>=DotMatrixFont.Width-16).Any(p=>p>0),"long line respects the display side margins");
        }
        var radioModel=document.models.Single(m=>m.kind==0);
        var glass=radioModel.parts.Single(p=>p.name=="screen");
        Check(glass.vertices.Where((_,i)=>i%3==2).Min()>-.1f,"glass is recessed inside the cabinet front rather than attached on top");
        Check(glass.vertices.Where((_,i)=>i%3==1).Max()<.30f,"screen leaves visible cabinet top margin");
        foreach(var point in new[]{(-.025f,.213f),(.233f,.213f),(-.025f,.283f),(.233f,.283f),(.104f,.248f)})
            Check(!radioModel.parts.Where(p=>p.name=="body").Any(p=>Occludes(p,point.Item1,point.Item2,-.087f)),"actual cabinet triangles leave the recessed glyph window visible");
        Console.WriteLine(checks+" embedded model geometry checks passed using production JSON reader. Runtime rendering remains a live check.");
    }

    private static bool Occludes(ModelPart part,float x,float y,float screenZ)
    {
        for(int i=0;i<part.triangles.Length;i+=3)
        {
            int a=part.triangles[i]*3,b=part.triangles[i+1]*3,c=part.triangles[i+2]*3;
            float ax=part.vertices[a],ay=part.vertices[a+1],bx=part.vertices[b],by=part.vertices[b+1],cx=part.vertices[c],cy=part.vertices[c+1];
            float determinant=(by-cy)*(ax-cx)+(cx-bx)*(ay-cy);
            if(Math.Abs(determinant)<.00000001f)continue;
            float wa=((by-cy)*(x-cx)+(cx-bx)*(y-cy))/determinant;
            float wb=((cy-ay)*(x-cx)+(ax-cx)*(y-cy))/determinant;
            float wc=1-wa-wb;
            if(wa>=0&&wb>=0&&wc>=0&&wa*part.vertices[a+2]+wb*part.vertices[b+2]+wc*part.vertices[c+2]<screenZ-.00001f)return true;
        }
        return false;
    }
}
