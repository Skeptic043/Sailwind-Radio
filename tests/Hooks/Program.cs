using System;
using SailwindRadio;
using SailwindRadio.Physical;
using UnityEngine;

internal static class Program
{
    private static int checks;
    private static void Check(bool result, string description)
    {
        checks++;
        if (!result) throw new Exception(description);
    }

    private static ShipItem Device(int kind, bool hangable = true)
    {
        var item = new ShipItem();
        item.transform.Components.Add(typeof(RadioItemController),
            new RadioItemController { transform = item.transform, State = new RadioState { Kind = kind } });
        if (hangable) item.transform.Components.Add(typeof(HangableItem), new HangableItem { transform = item.transform });
        return item;
    }

    public static void Main()
    {
        var target = new Component();
        var hook = new ShipItemLampHook { transform = target.transform };
        target.transform.Components.Add(typeof(ShipItemLampHook), hook);
        var radio = Device(0);
        Check(RadioNativeHooks.AllowsHookTarget(radio, target), "Radio can target a native hook without donor ID, name or third-party component");
        Check(!RadioNativeHooks.AllowsHookTarget(radio, new Component()), "ordinary targets retain native placement policy");
        Check(!RadioNativeHooks.AllowsHookTarget(radio, null), "missing pointer target is ignored");
        Check(!RadioNativeHooks.AllowsHookTarget(null, target), "missing held item is ignored");
        Check(!RadioNativeHooks.AllowsHookTarget(new ShipItem(), target), "unrelated native items remain unchanged");
        Check(!RadioNativeHooks.AllowsHookTarget(Device(0, false), target), "radio requires its actual native hangable component");
        for (int kind = 1; kind <= 3; kind++)
            Check(!RadioNativeHooks.AllowsHookTarget(Device(kind), target), "speakers are excluded even with a hangable component: " + kind);
        radio.GetComponent<RadioItemController>().State = null;
        Check(!RadioNativeHooks.AllowsHookTarget(radio, target), "uninitialized controller is ignored");
        radio.GetComponent<RadioItemController>().State = new RadioState();
        radio.GetComponent<HangableItem>().Destroyed = true;
        Check(!RadioNativeHooks.AllowsHookTarget(radio, target), "destroyed native component cannot grant placement");
        radio.GetComponent<HangableItem>().Destroyed = false;
        hook.Destroyed = true;
        Check(!RadioNativeHooks.AllowsHookTarget(radio, target), "destroyed hook cannot grant placement");
        hook.Destroyed = false;
        radio.Destroyed = true;
        Check(!RadioNativeHooks.AllowsHookTarget(radio, target), "destroyed radio cannot grant placement");
        Console.WriteLine($"Hook target checks passed: {checks}. Synthetic component checks, not live pointer or physics acceptance.");
    }
}
