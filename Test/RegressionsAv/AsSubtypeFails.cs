﻿using System.Diagnostics.Contracts;
class TestType
{
	public int a;
	public TestType()
	{
		a = 0;
	}
}

class SubType : TestType
{
	public int x;
	public SubType()
	{
		x = 7;
	}
}

class TestAs
{
	public static int Foo(object a)
	{
		var v = a as SubType;
		Contract.Assert(v != null);
		int s = v.x;
		return s;
	}

	public static void Main()
	{
		TestType v1 = new TestType();
		Foo(v1);
		return;
	}
}

