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
                    if(model.kind==0&&(part.name=="control_master"||part.name=="control_local"))
                        Check(part.vertices.Where((_,i)=>i%3==1).Min()>.336f,"radio top knob targets rise clear of the shortened pickup collider");
                    else
                    {
                        float front=model.kind==1?-.12f:model.kind==2?-.2f:model.kind==3?-.375f:-.1f;
                        Check(part.vertices.Where((_,i)=>i%3==2).Min()<front,"front control protrudes beyond native body collider");
                    }
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
        Check(DotMatrixFont.CanRender("Across the Blue 123")&&DotMatrixFont.CanRender("Joey Bada$$")&&DotMatrixFont.CanRender("Curren$y"),"dollar-sign artist metadata stays in the amber dot alphabet");
        const string LongPlaytestTitle="baby jesus, da baby - billion dollar baby, song she loves me [prod. by sean the first]";
        Check(DotMatrixFont.CanRender(LongPlaytestTitle),"bracketed production credit stays in the amber bitmap rather than an overflowing fallback TextMesh");
        Check(!DotMatrixFont.CanRender("海辺の音楽"),"unsupported scripts explicitly select the unchanged-font fallback");
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
        var longTitle=new MetadataMarquee();
        longTitle.Set(true,LongPlaytestTitle,"Joey Bada$$","",900);
        Check(longTitle.Advance(900)&&longTitle[0].Length==MetadataMarquee.VisibleCharacters&&
            DotMatrixFont.Rasterize(longTitle[0],longTitle[1],"").Any(p=>p>0),
            "long bracketed metadata is clipped to a bitmap viewport before painting");
        var marquee=new MetadataMarquee();
        marquee.Set(true,"ABCDEFGHIJKLMNOPQRSTUVWX","12345678901234567890","ALBUM",100);
        Check(marquee.Advance(100)&&marquee[0]=="ABCDEFGHIJKLMNOP"&&marquee[1]=="1234567890123456","marquee first frame shows fixed-size sixteen-character windows");
        Check(!marquee.Advance(101.99)&&marquee[0]=="ABCDEFGHIJKLMNOP","initial two-second dwell causes no bitmap rebuild");
        Check(marquee.Advance(102.35)&&marquee[0]=="BCDEFGHIJKLMNOPQ"&&marquee[1]=="2345678901234567","long lines advance together on one clock");
        Check(!marquee.Advance(102.4),"ordinary intermediate frames do not allocate or upload another bitmap");
        Check(marquee.Advance(104.8)&&marquee[0]=="IJKLMNOPQRSTUVWX"&&marquee[1]=="5678901234567890","shorter scrolling line holds at its suffix while the longest line reaches its end");
        Check(!marquee.Advance(106.79),"all lines share the final two-second dwell");
        Check(marquee.Advance(106.81)&&marquee[0]=="ABCDEFGHIJKLMNOP"&&marquee[1]=="1234567890123456","all lines restart together after the longest cycle");
        marquee.Set(false,"ABCDEFGHIJKLMNOPQRSTUVWX","12345678901234567890","ALBUM",107);
        Check(marquee.Advance(107)&&marquee[0]==""&&marquee[1]=="","power off blanks every metadata line");
        Check(!marquee.Advance(10000),"off display never uploads periodic empty frames");
        marquee.Set(true,"ABCDEFGHIJKLMNOPQRSTUVWX","12345678901234567890","ALBUM",10000);
        Check(marquee.Advance(10000)&&marquee[0]=="ABCDEFGHIJKLMNOP"&&!marquee.Advance(10001),"power on restarts readable initial dwell");
        marquee.Set(true,"NEW TITLE","12345678901234567890","ALBUM",10002);
        Check(marquee.Advance(10002)&&marquee[0]=="NEW TITLE"&&marquee[1]=="1234567890123456","track change restarts every line together");
        marquee.Set(true,"123456789012345😀Z","12345678901234567890","ALBUM",11000);
        marquee.Advance(11000);
        Check(marquee[0].EndsWith("😀"),"viewport boundary keeps a surrogate pair intact");
        for(int line=0;line<3;line++)
        {
            string[] text={"","",""};text[line]=new string('W',MetadataMarquee.VisibleCharacters);
            var raster=DotMatrixFont.Rasterize(text[0],text[1],text[2]);
            Check(raster.Any(p=>p>0),"each full marquee window paints at the fixed glyph size");
            Check(!raster.Where((p,i)=>i%DotMatrixFont.Width<16||i%DotMatrixFont.Width>=DotMatrixFont.Width-16).Any(p=>p>0),"fixed-size line respects the display side margins");
        }
        var radioModel=document.models.Single(m=>m.kind==0);
        var glass=radioModel.parts.Single(p=>p.name=="screen");
        Check(glass.vertices.Where((_,i)=>i%3==2).Min()>-.1f&&glass.vertices.Where((_,i)=>i%3==2).Min()<-.09f,"glass sits in a shallow inset inside the cabinet front");
        Check(glass.vertices.Where((_,i)=>i%3==0).Min()>-.28f&&glass.vertices.Where((_,i)=>i%3==0).Max()<.28f,
            "screen glass stays inside the front baffle side margins");
        Check(glass.vertices.Where((_,i)=>i%3==1).Max()<.27f&&glass.vertices.Where((_,i)=>i%3==1).Min()>.10f,
            "screen occupies the center of the front face with a lower control margin");
        foreach(var point in new[]{(-.085f,.115f),(.235f,.115f),(-.085f,.245f),(.235f,.245f),(.075f,.18f)})
            Check(!radioModel.parts.Where(p=>p.name=="body").Any(p=>Occludes(p,point.Item1,point.Item2,-.099f)),"cabinet and bezel leave the lowered shallow glyph window visible");
        var master=radioModel.parts.Single(p=>p.name=="control_master");
        var local=radioModel.parts.Single(p=>p.name=="control_local");
        var power=radioModel.parts.Single(p=>p.name=="control_power");
        Check(master.pivot[0]<-.17f&&local.pivot[0]>.17f&&
            master.vertices.Where((_,i)=>i%3==1).Min()>.336f&&local.vertices.Where((_,i)=>i%3==1).Min()>.336f,
            "left and right top knobs stand clear of the shortened pickup collider at y=.33");
        Check(Math.Abs(master.pivot[0]+local.pivot[0])<.00001f&&
            Math.Abs(master.pivot[2]-local.pivot[2])<.00001f&&
            master.pivot[2]>-.04f,
            "top volume knobs are symmetric and moved toward the handle");
        var topLabels=radioModel.parts.Single(p=>p.name=="body"&&
            document.materials[p.material].name=="Ivory control inlay");
        Check(topLabels.vertices.Where((_,i)=>i%3==2).All(z=>z>-.075f&&z<-.055f),
            "top control labels moved back with their knobs and remain on the cabinet");
        var brass=radioModel.parts.Single(p=>p.name=="body"&&
            document.materials[p.material].name=="Brushed warm brass");
        var railPoints=Enumerable.Range(0,brass.vertices.Length/3)
            .Select(i=>(x:brass.vertices[i*3],y:brass.vertices[i*3+1],z:brass.vertices[i*3+2]))
            .Where(p=>Math.Abs(p.x)>.265f&&Math.Abs(p.x)<.275f&&p.y>.02f&&p.y<.05f&&p.z>-.12f&&p.z<-.105f).ToArray();
        Check(railPoints.Any(p=>p.x<0)&&railPoints.Any(p=>p.x>0)&&
            railPoints.Where(p=>p.x<0).All(p=>railPoints.Any(q=>q.x>0&&Math.Abs(q.x+p.x)<.00001f&&
                Math.Abs(q.y-p.y)<.00001f&&Math.Abs(q.z-p.z)<.00001f)),
            "left and right front brass border lines mirror each other");
        Check(power.vertices.Where((_,i)=>i%3==0).Min()>.19f&&
            power.vertices.Where((_,i)=>i%3==1).Max()<glass.vertices.Where((_,i)=>i%3==1).Min()&&
            power.vertices.Where((_,i)=>i%3==2).Min()<-.13f,
            "larger front-facing power cap is at the lower right and protrudes ahead of the pickup collider");
        Check(Math.Abs((power.vertices.Where((_,i)=>i%3==0).Min()+power.vertices.Where((_,i)=>i%3==0).Max())*.5f-.228f)<.00001f&&
            Math.Abs((power.vertices.Where((_,i)=>i%3==1).Min()+power.vertices.Where((_,i)=>i%3==1).Max())*.5f-.058f)<.00001f,
            "front power button moves slightly left and up while retaining its control mesh");
        Check(master.vertices.Where((_,i)=>i%3==1).Min()>glass.vertices.Where((_,i)=>i%3==1).Max()&&
            local.vertices.Where((_,i)=>i%3==1).Min()>glass.vertices.Where((_,i)=>i%3==1).Max()&&
            radioModel.parts.Where(p=>p.name=="control_previous"||p.name=="control_playpause"||p.name=="control_next"||
                p.name=="control_shuffle"||p.name=="control_collections").All(p=>p.vertices.Where((_,i)=>i%3==1).Min()>.02f&&
                    p.vertices.Where((_,i)=>i%3==2).Min()<-.13f),
            "all five front actions remain separated from the screen and project ahead of the pickup collider");
        var grille=radioModel.parts.Single(p=>p.name=="body"&&document.materials[p.material].name=="Charcoal speaker cloth");
        Check(grille.vertices.Where((_,i)=>i%3==0).Min()>-.273f&&
            grille.vertices.Where((_,i)=>i%3==0).Max()<-.105f,
            "radio grille stays inside the front baffle and clear of the screen bezel");
        Check(Math.Abs((grille.vertices.Where((_,i)=>i%3==0).Min()+grille.vertices.Where((_,i)=>i%3==0).Max())*.5f+.185f)<.00001f,
            "radio speaker cloth is centered a little farther right in its front-face area");
        var satellite=document.models.Single(m=>m.kind==1);
        var volume=satellite.parts.Single(p=>p.name=="control_volume");
        var marker=satellite.parts.Single(p=>p.name=="icon_volume");
        Check(marker.vertices.Where((_,i)=>i%3==1).Max()<=volume.vertices.Where((_,i)=>i%3==1).Min()&&
            volume.vertices.Where((_,i)=>i%3==1).Min()-marker.vertices.Where((_,i)=>i%3==1).Max()<.004f,
            "small speaker volume marker sits directly below the dial");
        Check(marker.vertices.Where((_,i)=>i%3==2).Min()>-.135f&&marker.vertices.Where((_,i)=>i%3==2).Max()<-.129f,
            "small speaker marker rests near the front baffle instead of floating in front of it");
        foreach(var (kind,name,faceBottom) in new[]{(2,"volume",.04125f),(3,"bass",.0495f)})
        {
            var speaker=document.models.Single(m=>m.kind==kind);
            var dial=speaker.parts.Single(p=>p.name=="control_"+name);
            var legend=speaker.parts.Single(p=>p.name=="icon_"+name);
            Check(legend.vertices.Where((_,i)=>i%3==1).Min()>faceBottom&&
                legend.vertices.Where((_,i)=>i%3==1).Max()<dial.vertices.Where((_,i)=>i%3==1).Min(),
                "large speaker and Wolfer dial legends remain fully on their front baffles below the controls");
        }
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
