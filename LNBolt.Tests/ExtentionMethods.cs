using System;
using System.Collections.Generic;

namespace LNBolt.Tests;

public static class ExtentionMethods
{
    private static readonly Random r = new();

    public static List<T> GetRandomFromList<T>(this List<T> l, int count, int maxCycleCount = 1000)
    {
        var response = new List<T>();
        if (l.Count <= count) return l;
        var randomMax = l.Count - 1;
        for (var i = 0; i < count; i++)
        {
            var found = false;
            var cycleCount = 0;
            while (!found)
            {
                cycleCount++;
                var randomItem = l[r.Next(randomMax)];
                //find nodes not in existing list, not self, and not any existing channel
                if (!response.Contains(randomItem))
                {
                    found = true;
                    response.Add(randomItem);
                }

                if (cycleCount >= maxCycleCount)
                    break;
            }
        }

        return response;
    }

    public static byte[] HexToBytes(this string data)
    {
        return Convert.FromHexString(data);
    }
    //public static byte[] TrimZeros(this byte[] data)
    //{
    //    int trimOffset = 0;
    //    for (int i = 0; i < data.Length; i++)
    //    {
    //        if (data[i] == 0)
    //        {
    //            trimOffset++;
    //        } 
    //        else
    //        {
    //            break;
    //        }
    //    }
    //    return data[trimOffset..];
    //}
}