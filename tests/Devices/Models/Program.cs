using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
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
        int[] expectedKinds=Enumerable.Range(0,16).ToArray();
        // The released kind set is add-only. Saves hold native items by kind slot, and the game cannot
        // load a save whose item slot is no longer registered, so a kind must never be removed.
        Check(document.version==2&&document.models.Select(m=>m.kind).OrderBy(k=>k).SequenceEqual(expectedKinds),
            "embedded model kinds changed. Released kinds are add-only because removing one breaks native loading of saves that hold it");
        Check(DeviceModel.Kinds.SequenceEqual(expectedKinds),"runtime registers exactly the kinds the model document contains");
        MaterialAllocationChecks(document);
        Check(document.models.Single(m=>m.kind==14).solids?.Length>0&&document.models.Single(m=>m.kind==15).solids?.Length>0&&
            document.models.Where(m=>m.solids!=null).All(m=>DeviceKind.Family(m.kind)!=0),
            "the Emerald woofer and large speaker carry solid boxes for things placed on their flat tops");
        foreach(var model in document.models)
        {
            var names=model.parts.Select(p=>p.name).ToArray();
            var role=DeviceKind.Role(model.kind);
            bool walnut=DeviceKind.Family(model.kind)==0;
            // Regional sets have no hard triangle cap. Their counts are reviewed with the art.
            if(walnut)
            {
                Check(model.parts.Sum(p=>p.triangles.Length/3)<=new[]{3000,1000,1800,2000}[model.kind],"device stays within authored triangle budget");
                Check(model.parts.Sum(p=>p.vertices.Length/3)<=new[]{3500,1600,2800,2400}[model.kind],"exported device vertices remain within a compact item budget");
                Check(model.bounds==null&&model.contact==null&&model.parts.All(p=>p.axis==null),"walnut devices keep their constant contact boxes and original knob rotation");
            }
            Check(names.Contains("screen")== (walnut&&role==DeviceRole.Radio),"only the walnut radio has an independently illuminated screen");
            // Regional radios carry anchors instead of display surfaces, and the runtime builds the rows there.
            Check((model.displays!=null)==(!walnut&&role==DeviceRole.Radio),"only regional radios carry display anchors");
            if(model.solids!=null)
                Check(model.solids.Length>0&&model.solids.All(b=>Enumerable.Range(0,3).All(a=>b.min[a]<b.max[a]&&b.min[a]>=model.bounds.min[a]-.001f&&b.max[a]<=model.bounds.max[a]+.001f)),
                    "solid boxes of kind "+model.kind+" lie inside its bounds");
            if(model.displays!=null)
                foreach(var display in model.displays)
                {
                    float[] along=display.type=="scroll"
                        ?new[]{-display.width*.5f+TextFit.ScrollMargin*display.height,display.width*.5f-TextFit.ScrollMargin*display.height}
                        :Enumerable.Range(0,display.cells).Select(i=>TrackDisplayText.CellX(i,display.cells,display.pitch)).ToArray();
                    var hidden=Enumerable.Range(0,along.Length).Where(i=>Hidden(model,Enumerable.Range(0,3).Select(a=>display.center[a]+display.normal[a]*.0012f+display.right[a]*along[i]).ToArray(),display.normal)).ToArray();
                    Check(hidden.Length==0,"every "+display.row+" cell of kind "+model.kind+" shows through the cabinet, hidden: "+string.Join(",",hidden));
                }
            if(model.displays!=null)
                Check(model.displays.Select(d=>d.row).OrderBy(r=>r).SequenceEqual(new[]{"artist","title"})&&
                    model.displays.All(d=>d.type==new[]{"","drum","splitflap","scroll"}[DeviceKind.Family(model.kind)]),
                    "regional radio "+model.kind+" has one title and one artist row of its family's display type");
            Check(names.Contains("control_power")&&names.Contains("icon_power"),"every device has visible power control");
            Check(names.Contains("control_master")== (role==DeviceRole.Radio)&&names.Contains("control_local")== (role==DeviceRole.Radio),"radio alone has master and local knobs");
            Check(names.Contains("control_bass")== (role==DeviceRole.Woofer)&&
                names.Contains("control_volume")== (role==DeviceRole.SmallSpeaker||role==DeviceRole.Speaker),"woofer has bass only while speakers have local volume");
            Check(model.parts.Where(p=>p.name=="body").Sum(p=>p.vertices.Length/3)<65535,"root mesh stays within Unity16bit index capacity");
            if(!walnut)
            {
                Check(model.bounds!=null,"regional device carries model bounds for its contact box");
                var vertices=model.parts.SelectMany(p=>Enumerable.Range(0,p.vertices.Length/3).Select(i=>new[]{p.vertices[i*3],p.vertices[i*3+1],p.vertices[i*3+2]})).ToArray();
                Check(Enumerable.Range(0,3).All(a=>vertices.All(v=>v[a]>=model.bounds.min[a]-.0001f&&v[a]<=model.bounds.max[a]+.0001f)),
                    "regional bounds contain every vertex of kind "+model.kind);
                Check(Math.Abs(model.bounds.min[1])<.0001f,"regional device origin sits at the bottom of its bounds");
                if(role==DeviceRole.SmallSpeaker)Check(Math.Abs(model.bounds.max[2])<.0001f,"regional small speaker back sits on the wall attachment plane");
                Check(model.contact!=null&&Enumerable.Range(0,3).All(a=>model.contact.min[a]>=model.bounds.min[a]-.0001f&&model.contact.max[a]<=model.bounds.max[a]+.0001f),
                    "regional contact box lies within the model bounds");
                if(role==DeviceRole.SmallSpeaker)Check(Math.Abs(model.contact.max[2])<.0001f,"regional small speaker contact box ends on the wall attachment plane");
                // Native GoPointer takes the first collider hit, so a pointer ray aimed into each control's face
                // must meet its padded trigger before the contact box. Sloped decks rule out a whole-box comparison.
                foreach(var name in model.parts.Select(p=>p.name).Where(n=>n.StartsWith("control_")).Distinct())
                {
                    var pieces=model.parts.Where(p=>p.name==name).ToArray();
                    var glyph=model.parts.SingleOrDefault(p=>p.name=="icon_"+name.Substring(8));
                    float[] face=pieces[0].axis??glyph?.normals.Take(3).ToArray();
                    Check(face!=null,"regional control "+name+" has a face direction");
                    float[] min=new float[3],max=new float[3];
                    for(int a=0;a<3;a++)
                    {
                        int axis=a;
                        float half=(axis==2?.018f:.012f)*.5f;
                        min[a]=pieces.Min(p=>p.vertices.Where((_,i)=>i%3==axis).Min())-half;
                        max[a]=pieces.Max(p=>p.vertices.Where((_,i)=>i%3==axis).Max())+half;
                    }
                    var centre=Enumerable.Range(0,3).Select(a=>(min[a]+max[a])*.5f).ToArray();
                    float trigger=FarthestAlong(centre,face,min,max),box=FarthestAlong(centre,face,model.contact.min,model.contact.max);
                    Check(trigger>box,"regional control "+name+" on kind "+model.kind+" reaches past the contact box by "+(trigger-box)*1000+" mm");
                }
                var icon=model.parts.Single(p=>p.name=="icon_power");
                Check(Enumerable.Range(0,icon.normals.Length/3).All(i=>icon.normals[i*3]==icon.normals[0]&&icon.normals[i*3+1]==icon.normals[1]&&icon.normals[i*3+2]==icon.normals[2]),
                    "regional power glyph is flat, so its normal places its light in front of its face");
                foreach(var knob in model.parts.Where(p=>new[]{"control_master","control_local","control_volume","control_bass"}.Contains(p.name)))
                {
                    Check(knob.axis!=null&&Math.Abs(knob.axis[0]*knob.axis[0]+knob.axis[1]*knob.axis[1]+knob.axis[2]*knob.axis[2]-1)<.0001f,"regional knob has a unit rotation axis");
                    float outward=Enumerable.Range(0,3).Sum(a=>knob.axis[a]*(knob.pivot[a]-(model.bounds.min[a]+model.bounds.max[a])*.5f));
                    Check(outward>0,"regional knob axis points out of the face the knob sits on");
                }
            }
            foreach(var part in model.parts)
            {
                Check(part.vertices.Length>0&&part.triangles.Length>0,"every visible part has geometry");
                Check(part.material>=0&&part.material<document.materials.Length,"every mesh resolves an authored material");
                Check(part.pivot.Length==3&&part.pivot.All(float.IsFinite),"all mesh pivots are explicit finite local coordinates");
                if(part.name.StartsWith("indicator_"))
                {
                    var control=model.parts.Where(p=>p.name=="control_"+part.name.Substring(10)).ToArray();
                    Check(control.All(c=>part.pivot.SequenceEqual(c.pivot)),"knob and indicator share the same rotation pivot");
                    Check(control.All(c=>(c.axis==null&&part.axis==null)||(c.axis!=null&&part.axis!=null&&c.axis.SequenceEqual(part.axis))),"knob and indicator share one rotation axis");
                    var axis=control[0].axis??new[]{0f,0f,-1f};
                    foreach(int a in Enumerable.Range(0,3).Where(a=>Math.Abs(axis[a])<.5f))
                    {
                        float min=control.Min(c=>c.vertices.Where((_,i)=>i%3==a).Min()),max=control.Max(c=>c.vertices.Where((_,i)=>i%3==a).Max());
                        Check(Math.Abs((min+max)/2-control[0].pivot[a])<.00001,"rotation pivot sits at knob face center, not whole device origin");
                    }
                    Check(!DeviceModel.HasOwnLightMaterial(control[0].name),"level knobs do not share button light state");
                }
                if(role==DeviceRole.SmallSpeaker)Check(part.vertices.Where((_,i)=>i%3==2).Max()<=.000001f,"satellite never extends behind its native wall contact plane");
                if(walnut&&part.name.StartsWith("control_"))
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
        var walnutOnly=JsonNode.Parse(ResourceText());
        walnutOnly["version"]=1;
        var walnutModels=walnutOnly["models"].AsArray();
        for(int i=walnutModels.Count-1;i>=0;i--)if((int)walnutModels[i]["kind"]>3)walnutModels.RemoveAt(i);
        walnutOnly["materials"]=new JsonArray(walnutOnly["materials"].AsArray().Take(8).Select(m=>m.DeepClone()).ToArray());
        Check(DeviceModel.Read(Stream(walnutOnly)).models.Length==4,"version 1 walnut documents still load");
        Check(Rejects(Edit(d=>d["version"]=1)),"version 1 cannot carry regional kinds");
        Check(Rejects(Edit(d=>RemoveKind(d,2))),"every walnut kind remains required");
        Check(Rejects(Edit(d=>Model(d,9).AsObject().Remove("bounds"))),"regional kinds need bounds");
        Check(Rejects(Edit(d=>Model(d,9)["bounds"]["min"][1]=1f)),"inverted bounds are rejected");
        Check(Rejects(Edit(d=>Model(d,10).AsObject().Remove("contact"))),"regional kinds need a contact box");
        Check(Rejects(Edit(d=>Model(d,10)["contact"]["min"][0]=1f)),"inverted contact boxes are rejected");
        Check(Rejects(Edit(d=>Part(d,7,"control_bass")["axis"]=new JsonArray(0f,0f,0f))),"zero rotation axes are rejected");
        Check(Rejects(Edit(d=>Model(d,5)["parts"].AsArray().Add(Part(d,5,"icon_power").DeepClone()))),"a lit icon cannot repeat across materials");
        Check(Rejects(Edit(d=>Part(d,9,"control_power")["pivot"]=new JsonArray(1f,0f,0f))),"pieces of one control share a pivot");
        Check(Rejects(Edit(d=>Model(d,7)["kind"]=16)),"kinds beyond the sixteen device slots are rejected");
        Check(Rejects(Edit(d=>Model(d,8).AsObject().Remove("displays"))),"regional radios need display anchors");
        Check(Rejects(Edit(d=>Model(d,0)["solids"]=new JsonArray(Model(d,14)["solids"][0].DeepClone()))),"walnut devices cannot carry solid boxes");
        Check(Rejects(Edit(d=>Model(d,14)["solids"]=new JsonArray())),"an empty solid list is rejected");
        Check(Rejects(Edit(d=>Model(d,14)["solids"][0]["max"][1]=5f)),"a solid box reaching outside the model bounds is rejected");
        Check(Rejects(Edit(d=>Model(d,9)["displays"]=Model(d,8)["displays"].DeepClone())),"speakers cannot carry display anchors");
        Check(Rejects(Edit(d=>Model(d,8)["displays"][0]["normal"]=new JsonArray(0f,1f,-1f))),"a display normal must be a unit vector");
        Check(Rejects(Edit(d=>Model(d,8)["displays"][0]["right"]=new JsonArray(-1f,0f,0f))),"a display frame that would mirror its text is rejected");
        Check(Rejects(Edit(d=>Model(d,4)["displays"][1]["row"]="title")),"each regional radio has one title and one artist row");
        Check(Rejects(Edit(d=>Model(d,4)["displays"][0]["type"]="nixie")),"unknown display types are rejected");
        Check(Rejects(Edit(d=>Model(d,4)["displays"][0].AsObject().Remove("radius"))),"drum rows need a wheel radius");
        Check(Rejects(Edit(d=>Model(d,8)["displays"][0]["cells"]=0)),"cell rows need at least one cell");
        Check(Rejects(Edit(d=>Model(d,12)["displays"][0]["height"]=0f)),"display rows need a positive height");
        DisplayTextChecks();
        MechanismChecks();
        DisplayPathChecks();
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
        var rubber=radioModel.parts.Single(p=>p.name=="body"&&document.materials[p.material].name=="Rubber and leather");
        var footPoints=Enumerable.Range(0,rubber.vertices.Length/3)
            .Select(i=>(x:rubber.vertices[i*3],y:rubber.vertices[i*3+1],z:rubber.vertices[i*3+2]))
            .Where(p=>p.y<-.005f).ToArray();
        Check(footPoints.Length>0&&footPoints.Min(p=>p.y)<-.009f&&
            footPoints.All(p=>Math.Abs(p.x)<.25f&&Math.Abs(p.z)<.09f)&&
            new[]{(-1,-1),(-1,1),(1,-1),(1,1)}.All(q=>footPoints.Any(p=>Math.Sign(p.x)==q.Item1&&Math.Sign(p.z)==q.Item2)),
            "four compact radio feet protrude beneath the cabinet bottom without spilling beyond its footprint");
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
        var satelliteWood=satellite.parts.Single(p=>p.name=="body"&&
            document.materials[p.material].name=="Oiled walnut");
        var satelliteBrass=satellite.parts.Single(p=>p.name=="body"&&
            document.materials[p.material].name=="Brushed warm brass");
        Check(satelliteWood.vertices.Where((_,i)=>i%3==2).Max()<-.007f&&
            satelliteBrass.vertices.Where((_,i)=>i%3==2).Max()>=-.000001f,
            "small speaker wall shoe projects clear of its recessed cabinet back without crossing the wall contact plane");
        Check(volume.vertices.Where((_,i)=>i%3==2).Min()>-.155f&&
            volume.vertices.Where((_,i)=>i%3==2).Min()<-.145f&&
            Math.Abs(volume.pivot[2]+.139f)<.00001f,
            "small speaker volume knob has a compact front projection and a matching control pivot");
        Check(marker.vertices.Where((_,i)=>i%3==1).Max()<=volume.vertices.Where((_,i)=>i%3==1).Min()&&
            volume.vertices.Where((_,i)=>i%3==1).Min()-marker.vertices.Where((_,i)=>i%3==1).Max()<.004f,
            "small speaker volume marker sits directly below the dial");
        Check(marker.vertices.Where((_,i)=>i%3==2).Min()>-.135f&&marker.vertices.Where((_,i)=>i%3==2).Max()<-.129f,
            "small speaker marker rests near the front baffle instead of floating in front of it");
        foreach(var (kind,name,faceBottom) in new[]{(2,"volume",.04125f),(3,"bass",.0495f)})
        {
            var speaker=document.models.Single(m=>m.kind==kind);
            var speakerRubber=speaker.parts.Single(p=>p.name=="body"&&
                document.materials[p.material].name=="Rubber and leather");
            var supportPoints=Enumerable.Range(0,speakerRubber.vertices.Length/3)
                .Select(i=>(x:speakerRubber.vertices[i*3],y:speakerRubber.vertices[i*3+1],z:speakerRubber.vertices[i*3+2]))
                .Where(p=>p.y<-.008f).ToArray();
            Check(supportPoints.Length>0&&supportPoints.Min(p=>p.y)<(kind==2?-.011f:-.014f)&&
                new[]{(-1,-1),(-1,1),(1,-1),(1,1)}.All(q=>supportPoints.Any(p=>Math.Sign(p.x)==q.Item1&&Math.Sign(p.z)==q.Item2)),
                "four visible speaker feet extend below the cabinet instead of remaining buried in its base");
            var dial=speaker.parts.Single(p=>p.name=="control_"+name);
            var legend=speaker.parts.Single(p=>p.name=="icon_"+name);
            Check(legend.vertices.Where((_,i)=>i%3==1).Min()>faceBottom&&
                legend.vertices.Where((_,i)=>i%3==1).Max()<dial.vertices.Where((_,i)=>i%3==1).Min(),
                "large speaker and Wolfer dial legends remain fully on their front baffles below the controls");
        }
        Console.WriteLine(checks+" embedded model geometry checks passed using production JSON reader. Runtime rendering remains a live check.");
    }

    private static void DisplayTextChecks()
    {
        Check(TrackDisplayText.Fold("  Café  déjà-vu (Live) — Ünïcode 海辺 ")=="CAFE DEJA-VU (LIVE) UNICODE","regional text folds case and accents and turns other symbols into single spaces");
        Check(TrackDisplayText.Fold("Joey Bada$$ & \"Friends\" #1 [prod. x_y]: ok? *!")=="JOEY BADA$$ & \"FRIENDS\" #1 [PROD. X_Y]: OK? *!","the cell punctuation set survives folding");
        Check(TrackDisplayText.Fold("@%+=~…")==""&&TrackDisplayText.Fold(null)=="","unsupported characters alone fold to an empty row");
        Check(TrackDisplayText.CutCells("THE TRADE WINDS",18)=="THE TRADE WINDS","text that fits is shown whole");
        Check(TrackDisplayText.CutCells("BRUDDANEM (FEAT. LIL DURK)",18)=="BRUDDANEM (FEAT. L","long text fills every cell, even mid-word, with no ellipsis");
        Check(TrackDisplayText.CutCells("SONG SHE LOVES ME PROD BY SEAN THE FIRST",18)=="SONG SHE LOVES ME","a cut landing on a space drops it");
        Check(TrackDisplayText.CutCells("",22)=="","an empty row stays empty");
        Check(TrackDisplayText.Cut("AAAA BBBB CCCC",t=>t.Length*1.5f,10)=="AAAA B"&&TrackDisplayText.Cut("AAAA BBBB",t=>t.Length*.85f,10)=="AAAA BBBB",
            "scroll text is cut by measured width at the minimum squeeze");
        Check(TrackDisplayText.Left("RADIO",8)=="RADIO   "&&TrackDisplayText.Left("",4)=="    "&&TrackDisplayText.Left("ABCD",4)=="ABCD","cell rows start their text at the left end");
        Check(Math.Abs(TextFit.Squeeze(11,10,.85f)-10f/11)<1e-6f&&TextFit.Squeeze(9,10,.85f)==1&&TextFit.Squeeze(20,10,.85f)==.85f,"scroll text squeezes only as far as it must, never past the minimum");
        var targets=new DisplayTargets();
        Check(targets.Set("TITLE","ARTIST")&&!targets.Set("TITLE","ARTIST")&&targets.Set("","")&&!targets.Set("",""),
            "a display animates only when its fitted text or power changes, so nothing replays");
    }

    private static void MechanismChecks()
    {
        int a=TrackDisplayText.Index('A'),c=TrackDisplayText.Index('C'),last=TrackDisplayText.Charset.Length-1;
        Check(TrackDisplayText.Charset.Length==55&&TrackDisplayText.Index(' ')==0&&a==1&&TrackDisplayText.Index('0')==27&&TrackDisplayText.Charset[last]=='_',
            "cells carry space, A to Z, digits, then punctuation");
        Check(TrackDisplayText.Steps(a,c)==2&&TrackDisplayText.Steps(c,a)==53&&TrackDisplayText.Steps(last,a)==2,"cells only move forward and wrap after the last character");
        // Fit from glyph metrics in font pixels.
        float scale=TextFit.CapScale(40,.03f,TextFit.DrumCap),room=TextFit.CellRoom*.02f;
        Check(Math.Abs(40*scale-.0246f)<1e-6f&&TextFit.FlapCap==.82f,"drum and split-flap capitals stand at 82% of the row");
        float vertical=TextFit.CellReach*.03f;
        TextFit.Glyph(25,20,scale,room,vertical,TextFit.FlapFloor,out float glyphScale,out float squeeze);
        Check(glyphScale==scale&&squeeze==1,"a glyph that fits its cell is not squeezed");
        TextFit.Glyph(40,20,scale,room,vertical,TextFit.FlapFloor,out glyphScale,out squeeze);
        Check(glyphScale==scale&&Math.Abs(40*scale*squeeze-room)<1e-6f&&squeeze>.5f,"a wide glyph squeezes on its own only as far as it needs, keeping its height");
        TextFit.Glyph(90,20,scale,room,vertical,TextFit.FlapFloor,out glyphScale,out squeeze);
        Check(squeeze==.5f&&glyphScale<scale&&Math.Abs(90*glyphScale*squeeze-room)<1e-6f,"only a split-flap glyph too wide at the 0.5 floor is drawn smaller");
        TextFit.Glyph(90,20,scale,room,vertical,TextFit.DrumFloor,out glyphScale,out squeeze);
        Check(squeeze==.7f&&Math.Abs(90*glyphScale*squeeze-room)<1e-6f,"drum glyphs stay at least 70% wide");
        TextFit.Glyph(25,30,scale,room,vertical,TextFit.FlapFloor,out glyphScale,out squeeze);
        Check(glyphScale<scale&&Math.Abs(30*glyphScale-vertical)<1e-6f,"a glyph reaching past its cell above or below the capital centre is drawn smaller so it stays inside");
        Check(Math.Abs(TextFit.ScrollScale(40,.02f)*40-.0124f)<1e-6f&&Math.Abs(TextFit.ScrollRoom(.3f,.02f)-.288f)<1e-6f,"scroll capitals stand at 62% of the row inside its margins");

        var flap=new FlapCell{Shown=a,Target=a};
        flap.Retarget(c,10,1/12.0);
        Check(flap.Update(9.99,out _)==0&&!flap.Flipping,"a flap waits for its staggered start");
        Check(flap.Update(10,out _)==0&&flap.Flipping&&flap.Next==a+1&&Math.Abs(flap.Progress(10.045)-.5f)<1e-4f,"the leaf falls over 90 ms");
        Check(flap.Update(10.0901,out bool arrived)==1&&flap.Shown==a+1&&!arrived&&flap.Flipping&&flap.Progress(10.0901)<.01f,
            "a leaf lands after 90 ms and the next falls once it has landed");
        Check(flap.Update(10.1802,out arrived)==1&&arrived&&flap.Settled&&flap.Shown==c,"the last landing reaches the target and the cell rests");
        flap=new FlapCell{Shown=last,Target=last};
        flap.Retarget(a,0,1/12.0);
        Check(flap.Update(5,out arrived)==2&&arrived&&flap.Shown==a,"a late update catches up through the wrap without skipping a leaf");

        var shaft=new DrumShaft(3);var drum=new System.Collections.Generic.List<DrumEvent>();
        shaft.Retarget(0,2,0,.1);shaft.Retarget(1,1,0,.1);
        shaft.Update(0,drum);
        Check(shaft.Running&&drum.Count==1&&!drum[0].Stop&&Math.Abs(drum[0].Moving-2f/3)<1e-6f,"the first step indexes every wheel off its target");
        Check(Math.Abs(shaft.Turn(0,.035)-.5f)<1e-4f&&shaft.Turn(2,.035)==0,"moving wheels turn with ease in and out while resting wheels stay");
        shaft.Update(.07,drum);
        Check(shaft.Shown.SequenceEqual(new[]{1,1,0})&&shaft.Turn(0,.08)==0&&drum.Count==1,"the turn ends at seventy percent of the step and the wheels rest");
        shaft.Update(.1,drum);
        Check(drum.Count==2&&Math.Abs(drum[1].Moving-1f/3)<1e-6f,"the next step moves only the wheel still short of its target");
        shaft.Update(.169,drum);
        Check(shaft.Running&&drum.Count==2,"the shaft keeps turning until the last wheel arrives");
        shaft.Update(.17,drum);
        Check(!shaft.Running&&drum.Count==3&&drum[2].Stop&&shaft.Shown.SequenceEqual(new[]{2,1,0}),"the shaft stops as the last wheel arrives");
        int d=TrackDisplayText.Index('D');
        Check(TrackDisplayText.ShortestStep(a,c)==1&&TrackDisplayText.ShortestStep(c,a)==-1&&TrackDisplayText.ShortestStep(a,a)==0&&
            TrackDisplayText.ShortestStep(last,a)==1&&TrackDisplayText.ShortestStep(a,last)==-1&&TrackDisplayText.Distance(a,last)==2,
            "drum wheels take the shorter way round, wrapping forward or backward");
        // The 55-character ring has no exact tie, so the rule is checked where the two ways differ by one.
        Check(TrackDisplayText.ShortestStep(0,27)==1&&TrackDisplayText.ShortestStep(0,28)==-1,"a wheel goes forward unless backward is shorter");
        var mixed=new DrumShaft(3);var mixedEvents=new System.Collections.Generic.List<DrumEvent>();
        mixed.Shown[0]=a;mixed.Shown[1]=d;mixed.Shown[2]=a;
        mixed.Retarget(0,d,0,.1);mixed.Retarget(1,a,0,.1);mixed.Retarget(2,last,0,.1);
        mixed.Update(0,mixedEvents);
        Check(mixed.Direction(0)==1&&mixed.Direction(1)==-1&&mixed.Direction(2)==-1,"wheels in one shaft step each in their own direction");
        double mixedNow=0;
        while(mixed.Running&&mixedNow<5){mixedNow+=1/120.0;mixed.Update(mixedNow,mixedEvents);}
        Check(mixed.Shown.SequenceEqual(new[]{d,a,last})&&mixedEvents.Count(e=>!e.Stop)==3&&mixedEvents.Last().Stop&&Math.Abs(mixedNow-(.2+.07))<.01,
            "a mixed update finishes together after the longest wheel's three steps");
        var wheelGlyph=new Glyph{Present=true,Advance=11,MinX=0,MaxX=10,MinY=0,MaxY=20,TopLeft=new UnityEngine.Vector2(0,1),TopRight=new UnityEngine.Vector2(1,1),BottomRight=new UnityEngine.Vector2(1,0)};
        var wheelVertices=new UnityEngine.Vector3[12];var wheelUvs=new UnityEngine.Vector2[12];
        float radius=.0576f,halfHeight=.0173f,wheelStep=2*halfHeight/radius;
        foreach(float angle in new[]{-.6f*wheelStep,.4f*wheelStep,-.4f*wheelStep,.6f*wheelStep})
        {
            GlyphMesh.Wheel(wheelVertices,wheelUvs,0,ref wheelGlyph,0,10,.0012f,1,radius,angle,halfHeight,3,.001f);
            Check(wheelVertices.All(v=>Math.Abs(v.y)<=halfHeight+1e-5f)&&wheelVertices.Any(v=>v.y!=0),
                "a glyph rolling "+(angle<0?"down":"up")+" stays inside the row window");
        }

        // Rows .3 m wide with title edges from -.15 and the carriage starting .06 m right of the left end.
        // The old title held three characters, so the pad sweeps .4 s to just past their right end.
        var scroll=new ScrollUpdate();var sounds=new System.Collections.Generic.List<ScrollSound>();
        float[] titleEdges={-.15f,-.14f,-.13f,-.12f},artistEdges={-.15f,-.14f};
        Check(ScrollUpdate.Smoothing(0)==0&&ScrollUpdate.Smoothing(3)==.4&&Math.Abs(ScrollUpdate.Smoothing(10)-.9)<1e-9,
            "the pass takes .09 s per old character of the longer row, at least .4 s, and none on a clear belt");
        scroll.Start(0,ScrollUpdate.Smoothing(3),5,new[]{1,0,2},new[]{1},-.09f,-.15f,-.1145f,.0035f,titleEdges,artistEdges,-.15f);
        scroll.Update(0,sounds);
        Check(sounds.Count==0&&scroll.Phase(.05)==ScrollPhase.Approach&&Math.Abs(scroll.Carriage(0)+.09f)<1e-6f&&float.IsNegativeInfinity(scroll.Cleared(.05))&&scroll.PadDown(.05)==0,
            "an update starts with the carriage where it is and moves it to the left end before erasing");
        Check(scroll.Phase(.2)==ScrollPhase.Lower&&Math.Abs(scroll.PadDown(.175)-.5f)<1e-5f&&float.IsNegativeInfinity(scroll.Cleared(.2)),"the felt pad lowers onto the belt first");
        Check(scroll.Phase(.5)==ScrollPhase.Smoothing&&scroll.PadDown(.5)==1&&Math.Abs(scroll.Cleared(.5)-(scroll.Carriage(.5)+.0035f))<1e-6f&&
            Math.Abs(scroll.Carriage(.5)+.1257324f)<1e-5f&&scroll.StylusReach(.5)==0,
            "old ink is cleared exactly under the pad's leading edge while the arm stays up");
        Check(Math.Abs(scroll.Carriage(.649)+.1145f)<1e-4f&&scroll.Phase(.7)==ScrollPhase.Lift&&Math.Abs(scroll.PadDown(.725)-.5f)<1e-5f,
            "the pass ends just past the old ink and the pad lifts");
        Check(scroll.Phase(1)==ScrollPhase.Jog&&scroll.Phase(1.2)==ScrollPhase.Carry&&scroll.Phase(1.5)==ScrollPhase.Title&&scroll.Phase(1.9)==ScrollPhase.Return&&
            scroll.Phase(2.1)==ScrollPhase.Artist&&scroll.Phase(2.3)==ScrollPhase.Home&&scroll.Phase(scroll.EndsAt)==ScrollPhase.Idle,
            "the belt jogs, the carriage carries back, writes the title, returns, writes the artist and goes home");
        Check(scroll.TitleWritten(1.2)==0&&scroll.TitleWritten(2)==3,"each title character is revealed as the stylus writes it");
        float previous=scroll.Carriage(0),largest=0;
        for(double t=0;t<=scroll.EndsAt+.1;t+=1/120.0){float x=scroll.Carriage(t);largest=Math.Max(largest,Math.Abs(x-previous));previous=x;}
        Check(largest<=.6f/120*1.05f+1e-5f,"the carriage never jumps, moving at most its travel speed between frames: "+largest);
        Check(Math.Abs(scroll.Carriage(scroll.EndsAt)+.15f)<1e-6f,"the carriage ends at home, the left end of the title row");
        float reach=scroll.StylusReach(0),reachStep=0,pad=scroll.PadDown(0),padStep=0;
        for(double t=0;t<=scroll.EndsAt+.1;t+=1/120.0)
        {
            float next=scroll.StylusReach(t),lowered=scroll.PadDown(t);
            reachStep=Math.Max(reachStep,Math.Abs(next-reach));padStep=Math.Max(padStep,Math.Abs(lowered-pad));reach=next;pad=lowered;
            Check(!(lowered>0&&next>0),"the stylus arm stays retracted while the pad is down");
        }
        Check(scroll.StylusReach(1.5)==1&&scroll.StylusReach(2.1)==2&&scroll.StylusReach(scroll.EndsAt)==0&&reachStep<.15f&&padStep<.15f,
            "the stylus lowers to each row it writes and retracts home, and the pad lowers and lifts, without jumping");
        Check(scroll.Scribble(1.55)==0&&Math.Abs(scroll.Scribble(1.35))>.9f&&scroll.Scribble(1.2)==0,"the stylus tip scribbles only while it draws a character");
        sounds.Clear();scroll.Update(.5,sounds);
        Check(sounds.SequenceEqual(new[]{ScrollSound.Eraser}),"the eraser sounds as the smoothing pass begins");
        sounds.Clear();scroll.Update(.95,sounds);
        Check(sounds.SequenceEqual(new[]{ScrollSound.EraserEnd,ScrollSound.Paper}),"the eraser ends with the pass and the belt jog sounds once");
        sounds.Clear();scroll.Update(scroll.EndsAt,sounds);
        Check(sounds.Count(s=>s==ScrollSound.Tick)==4&&sounds.Count(s=>s==ScrollSound.Stroke)==4&&sounds.Last()==ScrollSound.End&&!scroll.Running,
            "every character ticks, a space has no stroke, a two-stroke character strokes twice, and the update ends");
        var clear=new ScrollUpdate();sounds.Clear();
        clear.Start(0,0,5,new[]{1},new int[0],-.15f,-.15f,.15f,.0035f,new[]{-.15f,-.14f},new[]{-.15f},-.15f);
        bool padMoved=false;
        for(double t=0;t<=clear.EndsAt+.05;t+=1/120.0){clear.Update(t,sounds);padMoved|=clear.PadDown(t)>0||clear.Phase(t)==ScrollPhase.Smoothing;}
        Check(!padMoved&&!sounds.Contains(ScrollSound.Eraser)&&sounds.Contains(ScrollSound.Paper),"a clear belt gets no smoothing pass and no eraser");

        var ledger=new VoiceLedger<string>(12);
        var poolA=Enumerable.Range(0,6).Select(i=>"a"+i).ToArray();var poolB=Enumerable.Range(0,6).Select(i=>"b"+i).ToArray();var poolC=new[]{"c0","c1"};
        object ownerA=new object(),ownerB=new object(),ownerC=new object();
        string stolen;
        for(int i=0;i<6;i++)Check(ledger.Claim(ownerA,poolA,i,100,out stolen)=="a"+i&&stolen==null,"a radio fills its own pool first");
        Check(ledger.Claim(ownerA,poolA,6,100,out stolen)=="a0"&&stolen==null&&ledger.Count==6,"a full radio steals its own oldest voice");
        for(int i=0;i<6;i++)ledger.Claim(ownerB,poolB,7+i,100,out stolen);
        Check(ledger.Count==12,"two radios reach the shared cap");
        Check(ledger.Claim(ownerC,poolC,13,100,out stolen)=="c0"&&stolen=="a1"&&ledger.Count==12,"past twelve voices the oldest anywhere is stolen");
        ledger.Release(ownerB);
        Check(ledger.Count==6&&ledger.Claim(ownerC,poolC,500,1,out stolen)=="c0"&&stolen==null&&ledger.Count==1,"released and finished voices free their places");
        var bursts=new SoundCoalescer();float gain;
        for(int i=0;i<10;i++)Check(!bursts.Add(i*.002,i==3?-1f:-5f,out _),"landings inside one window start no voice yet");
        Check(!bursts.Flush(.024,out _)&&bursts.Flush(.025,out gain)&&Math.Abs(gain-6f)<1e-6f&&!bursts.Pending,
            "ten landings in one window start one voice at the loudest gain plus 10 log10 of the count, capped at +6 dB");
        Check(!bursts.Add(.03,-9f,out _)&&!bursts.Add(.04,-6f,out _)&&bursts.Add(.06,-3f,out gain)&&Math.Abs(gain-(-6f+10f*(float)Math.Log10(2)))<1e-5f&&bursts.Pending,
            "a later landing closes the earlier window as one voice and opens the next");
        Check(bursts.Flush(.09,out gain)&&Math.Abs(gain+3f)<1e-6f&&!bursts.Flush(1,out _),"a lone landing keeps its own gain and each window starts once");

        var glyph=new Glyph{Present=true,Advance=11,MinX=0,MaxX=10,MinY=0,MaxY=20,BottomLeft=new UnityEngine.Vector2(0,0),BottomRight=new UnityEngine.Vector2(1,0),
            TopLeft=new UnityEngine.Vector2(0,1),TopRight=new UnityEngine.Vector2(1,1)};
        var vertices=new UnityEngine.Vector3[4];var uvs=new UnityEngine.Vector2[4];
        GlyphMesh.Centred(vertices,uvs,0,ref glyph,0,10,.001f,1,0,-GlyphMesh.Open,0,GlyphMesh.Open,GlyphMesh.Open);
        Check(Math.Abs(vertices[0].y)<1e-7f&&Math.Abs(vertices[1].y-.01f)<1e-7f&&Math.Abs(uvs[0].y-.5f)<1e-6f&&Math.Abs(uvs[1].y-1)<1e-6f&&Math.Abs(vertices[0].x+.005f)<1e-7f,
            "a split-flap half is trimmed at the split line with its atlas coordinates cut to match");
        GlyphMesh.Centred(vertices,uvs,0,ref glyph,0,10,.001f,1,0,-.003f,-GlyphMesh.Open,.003f,.004f);
        Check(Math.Abs(vertices[0].x+.003f)<1e-7f&&Math.Abs(uvs[0].x-.2f)<1e-6f&&Math.Abs(vertices[1].y-.004f)<1e-7f,"split-flap glyphs are trimmed to their leaf on every side");
        GlyphMesh.Pen(vertices,uvs,0,ref glyph,0,10,.001f,1,0,-1,.004f);
        Check(Math.Abs(vertices[2].x-.004f)<1e-7f&&Math.Abs(uvs[2].x-.4f)<1e-6f,"scroll ink is revealed up to the stylus with matching atlas coordinates");
        GlyphMesh.Pen(vertices,uvs,0,ref glyph,0,10,.001f,1,0,.02f,1);
        Check(vertices.All(v=>v==UnityEngine.Vector3.zero),"fully erased ink collapses to nothing");

        TimingSmoke();
    }

    // Two seconds of 120 Hz frames through a full split-flap, drum and scroll update, with the row geometry
    // the display writes each frame. The pure layer must not allocate while it runs.
    private static void TimingSmoke()
    {
        var flaps=Enumerable.Range(0,36).Select(_=>new FlapCell()).ToArray();
        var shaft=new DrumShaft(44);var drum=new System.Collections.Generic.List<DrumEvent>(8);
        var scroll=new ScrollUpdate();var sounds=new System.Collections.Generic.List<ScrollSound>(16);
        var bursts=new SoundCoalescer();
        var glyph=new Glyph{Present=true,Advance=11,MinX=0,MaxX=10,MinY=0,MaxY=20,TopLeft=new UnityEngine.Vector2(0,1),TopRight=new UnityEngine.Vector2(1,1),BottomRight=new UnityEngine.Vector2(1,0)};
        var vertices=new UnityEngine.Vector3[44*24];var uvs=new UnityEngine.Vector2[vertices.Length];
        float[] edges=Enumerable.Range(0,27).Select(i=>-.15f+i*.01f).ToArray();
        for(int i=0;i<flaps.Length;i++)flaps[i].Retarget(TrackDisplayText.Index('Z'),i*.035,1/12.0);
        for(int i=0;i<44;i++)shaft.Retarget(i,TrackDisplayText.Index('Z'),0,1/12.0);
        scroll.Start(0,ScrollUpdate.Smoothing(26),5.5,new int[26],new int[26],0,-.15f,.15f,.0035f,edges,edges,-.15f);
        void Frame(double now)
        {
            foreach(var f in flaps){if(f.Update(now,out bool arrived)>0)bursts.Add(now,arrived?2:-4,out _);f.Progress(now);}
            bursts.Flush(now,out _);
            drum.Clear();shaft.Update(now,drum);
            sounds.Clear();scroll.Update(now,sounds);scroll.Carriage(now);scroll.Cleared(now);scroll.StylusReach(now);scroll.Scribble(now);scroll.PadDown(now);
            for(int w=0;w<44;w++)
            {
                float turn=shaft.Turn(w,now);
                for(int k=0;k<6;k++){GlyphMesh.Centred(vertices,uvs,(w*6+k)*4,ref glyph,w*.01f,10,.001f,.9f,0,-GlyphMesh.Open,-.01f,GlyphMesh.Open,.01f);GlyphMesh.Wrap(vertices,(w*6+k)*4,4,.05f,turn*.3f,.001f);}
            }
            GlyphMesh.Hinge(vertices,0,72,-.0014f,-1.2f);
        }
        Frame(0);
        var watch=new System.Diagnostics.Stopwatch();
        long before=GC.GetAllocatedBytesForCurrentThread();
        watch.Start();
        int frames=0;
        for(double t=1/120.0;t<=2;t+=1/120.0){Frame(t);frames++;}
        watch.Stop();
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Check(allocated==0,"display mechanisms and row geometry allocate nothing per frame, found "+allocated+" bytes");
        Console.WriteLine("Display mechanism smoke: "+(watch.Elapsed.TotalMilliseconds/frames).ToString("F4")+" ms mean per frame over "+frames+
            " frames of 36 flaps, 44 drum wheels and a scroll update, 0 bytes allocated.");
    }

    // True when the cabinet body lies in front of origin along direction.
    private static bool Hidden(ModelDefinition model,float[] origin,float[] direction)
    {
        foreach(var part in model.parts.Where(p=>p.name=="body"))
            for(int i=0;i<part.triangles.Length;i+=3)
            {
                float[] a=Corner(part,part.triangles[i]),b=Corner(part,part.triangles[i+1]),c=Corner(part,part.triangles[i+2]);
                float[] e1=Minus(b,a),e2=Minus(c,a),h=Cross(direction,e2);
                float det=Dot(e1,h);
                if(Math.Abs(det)<1e-12f)continue;
                float[] t=Minus(origin,a);
                float u=Dot(t,h)/det;
                if(u<0||u>1)continue;
                float[] q=Cross(t,e1);
                float v=Dot(direction,q)/det;
                if(v<0||u+v>1)continue;
                if(Dot(e2,q)/det>1e-5f)return true;
            }
        return false;
    }
    private static float[] Corner(ModelPart part,int index)=>new[]{part.vertices[index*3],part.vertices[index*3+1],part.vertices[index*3+2]};
    private static float[] Minus(float[] a,float[] b)=>new[]{a[0]-b[0],a[1]-b[1],a[2]-b[2]};
    private static float[] Cross(float[] a,float[] b)=>new[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};
    private static float Dot(float[] a,float[] b)=>a[0]*b[0]+a[1]*b[1]+a[2]*b[2];

    // The reported Run The Jewels rows through fit, targets and the mechanism to completion, read back cell by cell.
    private static void DisplayPathChecks()
    {
        const string Title="A Report to the Shareholders / Kill Your Masters",Artist="Run The Jewels";
        foreach(var (cells,pitch) in new[]{(22,.0173f),(18,.018184f)})
        {
            string title=TrackDisplayText.Left(TrackDisplayText.CutCells(TrackDisplayText.Fold(Title),cells),cells);
            string artist=TrackDisplayText.Left(TrackDisplayText.CutCells(TrackDisplayText.Fold(Artist),cells),cells);
            var targets=new DisplayTargets();
            Check(targets.Set(title,artist)&&targets.Title.StartsWith("A REPORT TO THE ")&&targets.Artist.StartsWith("RUN THE JEWELS "),
                "the fitted rows keep their first characters with "+cells+" cells");
            var shaft=new DrumShaft(cells*2);var events=new System.Collections.Generic.List<DrumEvent>();
            for(int i=0;i<cells;i++){shaft.Retarget(i,TrackDisplayText.Index(targets.Title[i]),0,1/12.0);shaft.Retarget(cells+i,TrackDisplayText.Index(targets.Artist[i]),0,1/12.0);}
            double now=0;
            while(shaft.Running&&now<60){now+=1/60.0;shaft.Update(now,events);}
            string shownTitle=new string(Enumerable.Range(0,cells).Select(i=>TrackDisplayText.Charset[shaft.Shown[i]]).ToArray());
            string shownArtist=new string(Enumerable.Range(0,cells).Select(i=>TrackDisplayText.Charset[shaft.Shown[cells+i]]).ToArray());
            string expected=TrackDisplayText.Fold(Title).Substring(0,cells).TrimEnd();
            Check(!shaft.Running&&shownTitle.TrimEnd()==expected&&shownArtist.TrimEnd()=="RUN THE JEWELS",
                "drum wheels fill the title row and settle on RUN THE JEWELS from cell 0 with "+cells+" cells: "+shownTitle+" / "+shownArtist);
            Check(Math.Abs(TrackDisplayText.CellX(0,cells,pitch)-pitch*.5f+cells*pitch*.5f)<1e-6f&&
                Math.Abs(TrackDisplayText.CellX(cells-1,cells,pitch)+pitch*.5f-cells*pitch*.5f)<1e-6f,
                "cell 0 sits at the row's left end and the last cell at its right end");
            var flaps=Enumerable.Range(0,cells).Select(_=>new FlapCell()).ToArray();
            for(int i=0;i<cells;i++)flaps[i].Retarget(TrackDisplayText.Index(targets.Title[i]),i*.035,1/12.0);
            for(double t=0;t<60&&!flaps.All(f=>f.Settled);t+=1/60.0)foreach(var f in flaps)f.Update(t,out _);
            Check(new string(flaps.Select(f=>TrackDisplayText.Charset[f.Shown]).ToArray()).TrimEnd()==expected,"split-flap cells settle on the same title from cell 0");
        }
        string scrollTitle=TrackDisplayText.Cut(TrackDisplayText.Fold(Title),t=>t.Length*.01f*TextFit.ScrollMinSqueeze,.2f);
        Check(scrollTitle=="A REPORT TO THE SHAREHO"&&TrackDisplayText.Cut(TrackDisplayText.Fold(Artist),t=>t.Length*.01f,.3f)=="RUN THE JEWELS",
            "scroll rows keep their first characters when cut");
    }

    // Farthest point of a box along the ray centre+t*face, or -infinity when the ray misses it.
    private static float FarthestAlong(float[] centre,float[] face,float[] min,float[] max)
    {
        float near=float.NegativeInfinity,far=float.PositiveInfinity;
        for(int a=0;a<3;a++)
        {
            if(Math.Abs(face[a])<1e-6f){if(centre[a]<min[a]||centre[a]>max[a])return float.NegativeInfinity;continue;}
            float t1=(min[a]-centre[a])/face[a],t2=(max[a]-centre[a])/face[a];
            near=Math.Max(near,Math.Min(t1,t2));far=Math.Min(far,Math.Max(t1,t2));
        }
        return near<=far?far:float.NegativeInfinity;
    }

    private static string ResourceText()
    {
        using var resource=typeof(DeviceModel).Assembly.GetManifestResourceStream("SailwindRadio.Models.devices.json");
        return new StreamReader(resource).ReadToEnd();
    }
    private static void MaterialAllocationChecks(ModelDocument document)
    {
        var wrongCreation=new System.Collections.Generic.List<int>();
        var brokenSharing=new System.Collections.Generic.List<string>();
        bool repeated=false;
        foreach(var model in document.models)
        {
            var created=new System.Collections.Generic.List<int>();
            var resolved=new System.Collections.Generic.Dictionary<int,object>();
            var material=DeviceModel.SharedMaterialResolver<object>(index=>{created.Add(index);return new object();});
            foreach(var part in model.parts)
            {
                var current=material(part.material);
                if(resolved.TryGetValue(part.material,out var previous))
                {
                    repeated=true;
                    if(!ReferenceEquals(previous,current))brokenSharing.Add(model.kind+":"+part.material);
                }
                else
                    resolved.Add(part.material,current);
            }
            var referenced=model.parts.Select(p=>p.material).Distinct().ToArray();
            if(created.Count!=referenced.Length||created.Except(referenced).Any())wrongCreation.Add(model.kind);
        }
        Check(wrongCreation.Count==0,"each kind creates exactly its referenced materials; wrong kinds: "+string.Join(",",wrongCreation));
        Check(repeated&&brokenSharing.Count==0,"repeated material indices share one instance per device; wrong kind:index pairs: "+string.Join(",",brokenSharing));
    }
    private static Stream Stream(JsonNode document)=>new MemoryStream(System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()));
    private static JsonNode Edit(Action<JsonNode> change){var document=JsonNode.Parse(ResourceText());change(document);return document;}
    private static bool Rejects(JsonNode document){try{DeviceModel.Read(Stream(document));return false;}catch(InvalidDataException){return true;}}
    private static JsonNode Model(JsonNode document,int kind)=>document["models"].AsArray().First(m=>(int)m["kind"]==kind);
    private static JsonNode Part(JsonNode document,int kind,string name)=>Model(document,kind)["parts"].AsArray().Last(p=>(string)p["name"]==name);
    private static void RemoveKind(JsonNode document,int kind)=>document["models"].AsArray().Remove(Model(document,kind));

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
