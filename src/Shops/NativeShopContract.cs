using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace SailwindRadio.Shops
{
    // Fail closed if a game update moves charging ahead of the native ownership transition.
    internal static class NativeShopContract
    {
        internal static bool KeeperReady(ShopArea area)
        {
            var keeper=area.GetShopkeeper();
            if(!keeper) return false;
            var region=typeof(Shopkeeper).GetField("parentRegion",BindingFlags.Instance|BindingFlags.NonPublic);
            var shop=typeof(Shopkeeper).GetField("shop",BindingFlags.Instance|BindingFlags.NonPublic);
            return region?.GetValue(keeper) is Region && ReferenceEquals(shop?.GetValue(keeper),area);
        }
        private sealed class Instruction
        {
            internal OpCode Code;
            internal MemberInfo Member;
        }
        internal static bool Supports(MethodInfo sale, MethodInfo sell)
        {
            try
            {
                var owner = Read(sell);
                int sold = owner.FindIndex(i=>i.Code==OpCodes.Stfld && i.Member?.Name=="sold" && i.Member.DeclaringType==typeof(ShipItem));
                int register = owner.FindIndex(i=>i.Member is MethodInfo m && m.DeclaringType==typeof(SaveablePrefab) && m.Name=="RegisterToSave");
                var transaction = Read(sale);
                int ownership = transaction.FindIndex(i=>Equals(i.Member,sell));
                int currency = transaction.FindIndex(i=>i.Member is FieldInfo f && f.DeclaringType==typeof(PlayerGold) && f.Name=="currency");
                int charge = transaction.FindIndex(i=>i.Code==OpCodes.Stelem_I4 || i.Code==OpCodes.Stind_I4);
                return sold>=0 && register>sold && ownership>=0 && currency>ownership && charge>currency;
            }
            catch { return false; }
        }

        private static List<Instruction> Read(MethodInfo method)
        {
            byte[] il=method?.GetMethodBody()?.GetILAsByteArray();
            if(il==null) throw new InvalidOperationException("Native method body unavailable");
            var codes=new Dictionary<short,OpCode>();
            foreach(var field in typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static))
                if(field.FieldType==typeof(OpCode)) { var code=(OpCode)field.GetValue(null); codes[code.Value]=code; }
            var result=new List<Instruction>();
            for(int at=0;at<il.Length;)
            {
                short value=il[at++];
                if(value==0xfe) value=(short)(0xfe00|il[at++]);
                var code=codes[value];
                var next=new Instruction { Code=code };
                int size;
                switch(code.OperandType)
                {
                    case OperandType.InlineNone:size=0;break;
                    case OperandType.ShortInlineBrTarget:case OperandType.ShortInlineI:case OperandType.ShortInlineVar:size=1;break;
                    case OperandType.InlineVar:size=2;break;
                    case OperandType.InlineI8:case OperandType.InlineR:size=8;break;
                    case OperandType.InlineSwitch:size=4+4*BitConverter.ToInt32(il,at);break;
                    default:size=4;break;
                }
                if(code.OperandType==OperandType.InlineMethod || code.OperandType==OperandType.InlineField)
                    next.Member=method.Module.ResolveMember(BitConverter.ToInt32(il,at));
                at+=size;
                result.Add(next);
            }
            return result;
        }
    }
}
