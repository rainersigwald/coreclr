// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.



using System;

internal struct VT
{
    public double[] vt_arr;
}

internal class CL
{
    public double[] cl_arr = new double[1000];
}

internal class DblArray3
{
    private static int s_LOH_GEN = 0;
    public static double[] s_arr;

    public static void f0()
    {
        s_arr = new double[1000];
        if (GC.GetGeneration(s_arr) != s_LOH_GEN)
        {
            Console.WriteLine("Generation {0}", GC.GetGeneration(s_arr));
            throw new Exception();
        }
    }

    public static void f1a()
    {
        double[,] arr = new double[1000, 1];
        if (GC.GetGeneration(arr) != s_LOH_GEN)
        {
            Console.WriteLine("Generation {0}", GC.GetGeneration(arr));
            throw new Exception();
        }
    }

    public static void f1b()
    {
        double[,] arr = new double[1, 1000];
        if (GC.GetGeneration(arr) != s_LOH_GEN)
        {
            Console.WriteLine("Generation {0}", GC.GetGeneration(arr));
            throw new Exception();
        }
    }


    public static void f1c()
    {
        double[,] arr = new double[5, 5];
        if (GC.GetGeneration(arr) != 0)
        {
            Console.WriteLine("Generation {0}", GC.GetGeneration(arr));
            throw new Exception();
        }
    }

    public static void f1d()
    {
        double[,] arr = new double[32, 32];
        if (GC.GetGeneration(arr) != s_LOH_GEN)
        {
            Console.WriteLine("Generation {0}", GC.GetGeneration(arr));
            throw new Exception();
        }
    }


    public static void f2a()
    {
        double[][] arr = new double[1][];
        arr[0] = new double[1000];
        if (GC.GetGeneration(arr) != 0)
        {
            Console.WriteLine("Generation {0}", GC.GetGeneration(arr));
            throw new Exception();
        }
    }

    public static void f2b()
    {
        double[][] arr = new double[1000][];
        arr[0] = new double[1000];
        if (GC.GetGeneration(arr) != 0)
        {
            Console.WriteLine("Generation {0}", GC.GetGeneration(arr));
            throw new Exception();
        }
    }

    public static void f3()
    {
        Array arr = Array.CreateInstance(typeof(double), 1000);
        if (GC.GetGeneration(arr) != 0)
        {
            Console.WriteLine("Generation {0}", GC.GetGeneration(arr));
            throw new Exception();
        }
    }

    public static void f4()
    {
        VT vt = new VT();
        vt.vt_arr = new double[1000];
        if (GC.GetGeneration(vt.vt_arr) != s_LOH_GEN)
        {
            Console.WriteLine("Generation {0}", GC.GetGeneration(vt.vt_arr));
            throw new Exception();
        }
    }

    public static void f5()
    {
        CL cl = new CL();
        if (GC.GetGeneration(cl.cl_arr) != s_LOH_GEN)
        {
            Console.WriteLine("Generation {0}", GC.GetGeneration(cl.cl_arr));
            throw new Exception();
        }
    }

    public static int Main()
    {
        Console.WriteLine(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"));
        if (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") == "x86")
        {
            s_LOH_GEN = 2;
        }
        try
        {
            f0();
            f1a();
            f1b();
            f1c();
            f1d();
            f2a();
            f2b();
            f3();
            f4();
            f5();
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
            Console.WriteLine(e.StackTrace);
            Console.WriteLine("FAILED");
            return -1;
        }
        Console.WriteLine("PASSED");
        return 100;
    }
}
