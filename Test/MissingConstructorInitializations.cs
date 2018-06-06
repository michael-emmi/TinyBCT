﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Test
{
    class MissingConstructorInitializations
    {
        // probar para cada boogie type

        public int int1 = 150;
        public int int2;

        //public bool bool1 = true;
        //public bool bool2;

        public MissingConstructorInitializations ref1;
        public MissingConstructorInitializations ref2;

        public float float1 = 1.60f;
        public float float2;

        public double double1 = 790.0691;
        public double double2;

        public MissingConstructorInitializations()
        {
            ref1 = this;
        }

        public static void test1()
        {
            //Contract.Assert(m.bool1 == true);
            //Contract.Assert(m.bool2 == false);
        }

        public static void testInt()
        {
            var m = new MissingConstructorInitializations();
            Contract.Assert(m.int1 == 150);
            Contract.Assert(m.int2 == 0);
        }

        public static void testRef()
        {
            var m = new MissingConstructorInitializations();
            Contract.Assert(m.ref1 == m);
            Contract.Assert(m.ref2 == null);
        }

        public static void testFloat()
        {
            var m = new MissingConstructorInitializations();
            Contract.Assert(m.float1 == 1.60f);
            Contract.Assert(m.float2 == 0.0f);
        }

        public static void testDouble()
        {
            var m = new MissingConstructorInitializations();
            Contract.Assert(m.double1 == 790.0691);
            Contract.Assert(m.double2 == 0.0);
        }
    }
}
