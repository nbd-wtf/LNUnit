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

LND_TAG=${1:-v0.18.5-beta}
LOOP_TAG=${2:-master}


wget -O ./Grpc/lightning.proto https://raw.githubusercontent.com/lightningnetwork/lnd/$LND_TAG/lnrpc/lightning.proto
wget -O ./Grpc/walletunlocker.proto https://raw.githubusercontent.com/lightningnetwork/lnd/$LND_TAG/lnrpc/walletunlocker.proto
wget -O ./Grpc/chainrpc/chainnotifier.proto https://raw.githubusercontent.com/lightningnetwork/lnd/$LND_TAG/lnrpc/chainrpc/chainnotifier.proto
wget -O ./Grpc/invoicesrpc/invoices.proto https://raw.githubusercontent.com/lightningnetwork/lnd/$LND_TAG/lnrpc/invoicesrpc/invoices.proto
wget -O ./Grpc/routerrpc/router.proto https://raw.githubusercontent.com/lightningnetwork/lnd/$LND_TAG/lnrpc/routerrpc/router.proto
wget -O ./Grpc/watchtowerrpc/watchtower.proto https://raw.githubusercontent.com/lightningnetwork/lnd/$LND_TAG/lnrpc/watchtowerrpc/watchtower.proto
wget -O ./Grpc/wtclientrpc/wtclient.proto https://raw.githubusercontent.com/lightningnetwork/lnd/$LND_TAG/lnrpc/wtclientrpc/wtclient.proto
wget -O ./Grpc/signrpc/signer.proto https://raw.githubusercontent.com/lightningnetwork/lnd/$LND_TAG/lnrpc/signrpc/signer.proto
wget -O ./Grpc/walletrpc/walletkit.proto https://raw.githubusercontent.com/lightningnetwork/lnd/$LND_TAG/lnrpc/walletrpc/walletkit.proto
wget -O ./Grpc/autopilotrpc/autopilot.proto https://raw.githubusercontent.com/lightningnetwork/lnd/$LND_TAG/lnrpc/autopilotrpc/autopilot.proto
wget -O ./Grpc/verrpc/verrpc.proto https://raw.githubusercontent.com/lightningnetwork/lnd/$LND_TAG/lnrpc/verrpc/verrpc.proto
wget -O ./Grpc/stateservice.proto https://raw.githubusercontent.com/lightningnetwork/lnd/$LND_TAG/lnrpc/stateservice.proto
wget -O ./Grpc/neutrinorpc/neutrino.proto https://raw.githubusercontent.com/lightningnetwork/lnd/$LND_TAG/lnrpc/neutrinorpc/neutrino.proto
wget -O ./Grpc/peersrpc/peers.proto https://raw.githubusercontent.com/lightningnetwork/lnd/$LND_TAG/lnrpc/peersrpc/peers.proto
wget -O ./Grpc/devrpc/dev.proto https://raw.githubusercontent.com/lightningnetwork/lnd/$LND_TAG/lnrpc/devrpc/dev.proto
# loopd
wget -O ./Grpc/looprpc/client.proto  https://raw.githubusercontent.com/lightninglabs/loop/$LOOP_TAG/looprpc/client.proto
wget -O ./Grpc/swapserverrpc/common.proto  https://raw.githubusercontent.com/lightninglabs/loop/$LOOP_TAG/swapserverrpc/common.proto
