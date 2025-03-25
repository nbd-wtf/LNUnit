 #! /bin/bash
mkdir -p ./Grpc/chainrpc
mkdir -p ./Grpc/invoicesrpc
mkdir -p ./Grpc/routerrpc
mkdir -p ./Grpc/watchtowerrpc
mkdir -p ./Grpc/wtclientrpc
mkdir -p ./Grpc/signrpc
mkdir -p ./Grpc/walletrpc
mkdir -p ./Grpc/autopilotrpc
mkdir -p ./Grpc/chainrpc
mkdir -p ./Grpc/verrpc
mkdir -p ./Grpc/neutrinorpc
mkdir -p ./Grpc/devrpc
mkdir -p ./Grpc/peersrpc
mkdir -p ./Grpc/looprpc
mkdir -p ./Grpc/swapserverrpc


wget -O ./Grpc/lightning.proto https://raw.githubusercontent.com/lightningnetwork/lnd/master/lnrpc/lightning.proto
wget -O ./Grpc/walletunlocker.proto https://raw.githubusercontent.com/lightningnetwork/lnd/master/lnrpc/walletunlocker.proto
wget -O ./Grpc/chainrpc/chainnotifier.proto https://raw.githubusercontent.com/lightningnetwork/lnd/master/lnrpc/chainrpc/chainnotifier.proto
wget -O ./Grpc/invoicesrpc/invoices.proto https://raw.githubusercontent.com/lightningnetwork/lnd/master/lnrpc/invoicesrpc/invoices.proto
wget -O ./Grpc/routerrpc/router.proto https://raw.githubusercontent.com/lightningnetwork/lnd/master/lnrpc/routerrpc/router.proto
wget -O ./Grpc/watchtowerrpc/watchtower.proto https://raw.githubusercontent.com/lightningnetwork/lnd/master/lnrpc/watchtowerrpc/watchtower.proto
wget -O ./Grpc/wtclientrpc/wtclient.proto https://raw.githubusercontent.com/lightningnetwork/lnd/master/lnrpc/wtclientrpc/wtclient.proto
wget -O ./Grpc/signrpc/signer.proto https://raw.githubusercontent.com/lightningnetwork/lnd/master/lnrpc/signrpc/signer.proto
wget -O ./Grpc/walletrpc/walletkit.proto https://raw.githubusercontent.com/lightningnetwork/lnd/master/lnrpc/walletrpc/walletkit.proto
wget -O ./Grpc/autopilotrpc/autopilot.proto https://raw.githubusercontent.com/lightningnetwork/lnd/master/lnrpc/autopilotrpc/autopilot.proto
wget -O ./Grpc/verrpc/verrpc.proto https://raw.githubusercontent.com/lightningnetwork/lnd/master/lnrpc/verrpc/verrpc.proto
wget -O ./Grpc/stateservice.proto https://raw.githubusercontent.com/lightningnetwork/lnd/master/lnrpc/stateservice.proto
wget -O ./Grpc/neutrinorpc/neutrino.proto https://raw.githubusercontent.com/lightningnetwork/lnd/master/lnrpc/neutrinorpc/neutrino.proto
wget -O ./Grpc/peersrpc/peers.proto https://raw.githubusercontent.com/lightningnetwork/lnd/master/lnrpc/peersrpc/peers.proto
wget -O ./Grpc/devrpc/dev.proto https://raw.githubusercontent.com/lightningnetwork/lnd/master/lnrpc/devrpc/dev.proto
# loopd
wget -O ./Grpc/looprpc/client.proto  https://raw.githubusercontent.com/lightninglabs/loop/master/looprpc/client.proto
wget -O ./Grpc/swapserverrpc/common.proto  https://raw.githubusercontent.com/lightninglabs/loop/master/swapserverrpc/common.proto
