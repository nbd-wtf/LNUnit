using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ServiceStack;
using ServiceStack.Text;

namespace LNBolt.Tests;

public class BigSizeTests
{
    [Test]
    public void ReadVectors()
    {
        var tests = File.ReadAllText("BigSizeTestVectors.json").FromJson<BigSizeTestVectors>();
        tests.PrintDump();
    }

    [Test]
    public void TestValidVectorsDecode()
    {
        var tests = File.ReadAllText("BigSizeTestVectors.json").FromJson<BigSizeTestVectors>();
        foreach (var test in tests.Tests.Where(x => x.exp_error.IsNullOrEmpty()))
        {
            var data = test.bytes.HexToBytes();
            var value = test.value;
            var name = test.name;
            $"{name} : {test.bytes} = {value}".Print();
            var result = BigSize.Parse(data);
            Assert.That(result.Value == value);
        }
    }

    [Test]
    public void TestValidVectorsEncode()
    {
        var tests = File.ReadAllText("BigSizeTestVectors.json").FromJson<BigSizeTestVectors>();
        foreach (var test in tests.Tests.Where(x => x.exp_error.IsNullOrEmpty()))
        {
            var data = test.bytes.HexToBytes();
            var value = test.value;
            var name = test.name;
            $"{name} : {test.bytes} = {value}".Print();
            var result = new BigSize(test.value);
            Assert.That(result.Encoding.ToHex() == test.bytes);
        }
    }

    [Test]
    public void TestValidVectorsDecodeNonCanonicalFails()
    {
        var tests = File.ReadAllText("BigSizeTestVectors.json").FromJson<BigSizeTestVectors>();
        foreach (var test in tests.Tests.Where(x => !x.exp_error.IsNullOrEmpty() && !x.exp_error.Contains("canonical")))
        {
            var data = test.bytes.HexToBytes();
            var value = test.value;
            var name = test.name;
            $"{name} : {test.bytes} Failure = '{test.exp_error}'".Print();
            try
            {
                var result = BigSize.Parse(data);
                Assert.Fail();
            }
            catch (Exception ex)
            {
                Assert.That(ex.Message == test.exp_error);
            }
        }
    }

    [Test]
    public void TestValidVectorsDecodeCanonicalFails()
    {
        var tests = File.ReadAllText("BigSizeTestVectors.json").FromJson<BigSizeTestVectors>();
        foreach (var test in tests.Tests.Where(x => !x.exp_error.IsNullOrEmpty() && x.exp_error.Contains("canonical")))
        {
            var data = test.bytes.HexToBytes();
            var value = test.value;
            var name = test.name;
            $"{name} : {test.bytes} Failure = '{test.exp_error}'".Print();
            try
            {
                var result = BigSize.Parse(data);
                Assert.Fail();
            }
            catch (Exception ex)
            {
                Assert.That(ex.Message == test.exp_error);
            }
        }
    }
}

internal class BigSizeTestVectors
{
    public Test[] Tests { get; set; }
}

internal class Test
{
    public string name { get; set; }
    public ulong value { get; set; }
    public string bytes { get; set; }
    public string exp_error { get; set; }
}