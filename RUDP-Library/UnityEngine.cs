
#if !UNITY_64
using System;

namespace UnityEngine
{
    public static class Debug
    {
        public static void LogError(object _msg)
        {
            Console.Error.WriteLine(_msg);
        }
        public static void Log(object _msg)
        {
            Console.WriteLine(_msg);
        }
    }

    namespace Assertions
    {
        public static class Assert
        {
            public static void IsNotNull(object _obj, string _msg = null)
            {
                if (_obj == null)
                    throw new Exception(_msg);
            }
            public static void AreEqual(object _output, object _expected, string _msg = null)
            {
                if (!_output.Equals(_expected))
                    throw new Exception(_msg);
            }
        }
    }
}
#endif