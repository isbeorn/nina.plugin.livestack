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

        public static ushort[] ToUShortArray(this float[] source) {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            ushort[] result = new ushort[source.Length];
            for (int i = 0; i < source.Length; i++) {
                result[i] = (ushort)Math.Clamp(source[i] * ushort.MaxValue, 0, ushort.MaxValue);
            }
            return result;
        }

        public static float[] ToFloatArray(this ushort[] source) {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            float[] result = new float[source.Length];
            for (int i = 0; i < source.Length; i++) {
                result[i] = source[i] / (float)ushort.MaxValue;
            }
            return result;
        }
    }
}

namespace NINA.Plugin.Livestack.Utility {
}