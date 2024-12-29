using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace NINA.Plugin.Livestack {

    public static class Extensions {

        public static IList<T> FromStringToList<T>(this string collection) {
            try {
                return JsonConvert.DeserializeObject<IList<T>>(collection, new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.Auto }) ?? new List<T>();
            } catch (Exception) {
                return new List<T>();
            }
        }

        public static string FromListToString<T>(this IList<T> l) {
            try {
                return JsonConvert.SerializeObject(l, new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.Auto }) ?? "";
            } catch (Exception) {
                return "";
            }
        }

        public static double StdDev<T>(this IEnumerable<T> list, Func<T, double> values) {
            var mean = 0.0;
            var sum = 0.0;
            var stdDev = 0.0;
            var n = 0;
            foreach (var value in list.Select(values)) {
                n++;
                var delta = value - mean;
                mean += delta / n;
                sum += delta * (value - mean);
            }
            if (1 < n)
                stdDev = Math.Sqrt(sum / (n - 1));

            return stdDev;
        }
    }
}

namespace NINA.Plugin.Livestack.Utility {
}