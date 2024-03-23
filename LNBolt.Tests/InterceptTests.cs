// using System;
// using System.Diagnostics;
// using System.Threading.Tasks;
// using NUnit.Framework;
// using LNDroneController.LND;
// using ServiceStack;
// using ServiceStack.Text;
// using Routerrpc;
// using Org.BouncyCastle.Math;
// using Org.BouncyCastle.Asn1.X9;
// using Org.BouncyCastle.Crypto.Parameters;
// using System.Linq;
// using LNBolt;
//
// namespace LNBolt.Tests
// {
//     public class InterceptTests
//     {
//         private LNDNodeConnection? Carol;
//         private LNDNodeConnection? Alice;
//
//         [SetUp]
//         public void Setup()
//         {
//             Carol = new LNDNodeConnection(new LNDSettings
//             {
//
//                 TLSCertPath = @"C:\Users\rjs\.polar\networks\1\volumes\lnd\carol\tls.cert",
//                 MacaroonPath = @"C:\Users\rjs\.polar\networks\1\volumes\lnd\carol\data\chain\bitcoin\regtest\admin.macaroon",
//                 GrpcEndpoint = $"https://127.0.0.1:10008",
//             });
//             Alice = new LNDNodeConnection(new LNDSettings
//             {
//
//                 TLSCertPath = @"C:\Users\rjs\.polar\networks\1\volumes\lnd\alice\tls.cert",
//                 MacaroonPath = @"C:\Users\rjs\.polar\networks\1\volumes\lnd\alice\data\chain\bitcoin\regtest\admin.macaroon",
//                 GrpcEndpoint = $"https://127.0.0.1:10004",
//             });
//         }
//
//         // [Test]
//         // public async Task TestInterception()
//         // {
//         //     var interceptor = new LNDSimpleHtlcInterceptorHandler(Carol, SettleBeforeDestinationIfKeysend);
//         //     await Task.Delay(1000 * 10000);
//         // }
//
//         private async Task<ForwardHtlcInterceptResponse> SettleBeforeDestinationIfKeysend(ForwardHtlcInterceptRequest data)
//         {
//             Debug.Print(data.Dump());
//             var onionBlob = data.OnionBlob.ToByteArray();
//             var decoder = new OnionBlob(onionBlob);
//             var sharedSecret = (await Alice.DeriveSharedKey(decoder.EphemeralPublicKey.ToHex())).SharedKey.ToByteArray();
//             var x = decoder.Peel(sharedSecret, null, data.PaymentHash.ToByteArray());
//
//             Debug.Print(x.hopPayload.Dump());
//             x.PrintDump();
//             //await decoder.Decode();
//             //decoder.PrintDump();
//             if (!x.hopPayload.OtherTLVs.Any(x => x.Type == 5482373484))
//             {
//                 var aliceInvoices = await Alice.ListInvoices(new Lnrpc.ListInvoiceRequest { PendingOnly = true});
//                 foreach(var i in aliceInvoices.Invoices)
//                 {
//                     Debug.Print(i.PaymentAddr.ToByteArray().ToHex());
//                 }
//                 var invoice = aliceInvoices.Invoices.FirstOrDefault(xx => xx.PaymentAddr.ToByteArray().ToHex() == x.hopPayload.PaymentData.PaymentSecret.ToHex());
//                 if (invoice == null)
//                 {
//                     return new ForwardHtlcInterceptResponse
//                     {
//                         Action = ResolveHoldForwardAction.Resume,
//                         IncomingCircuitKey = data.IncomingCircuitKey,
//                     };
//                 }
//                 else
//                 {
//                     var secret = invoice.RPreimage.ToByteArray();
//                     return new ForwardHtlcInterceptResponse
//                     {
//                         Action = ResolveHoldForwardAction.Settle,
//                         IncomingCircuitKey = data.IncomingCircuitKey,
//                         Preimage = Google.Protobuf.ByteString.CopyFrom(secret)
//                     };
//                 }
//                     
//                 
//             }
//             else
//             {
//                 var keySendPreimage = x.hopPayload.OtherTLVs.First(x => x.Type == 5482373484).Value;
//                 return new ForwardHtlcInterceptResponse
//                 {
//                     Action = ResolveHoldForwardAction.Settle,
//                     IncomingCircuitKey = data.IncomingCircuitKey,
//                     Preimage = Google.Protobuf.ByteString.CopyFrom(keySendPreimage)
//                 };
//             }
//
//
//
//         }
//     }
// }

