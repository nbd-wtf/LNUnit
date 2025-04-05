FROM debian:stable-slim as build

#prereq
RUN apt-get update -y \
  && apt-get install -y curl wget gosu build-essential cmake pkgconf python3 libevent-dev libboost-dev libzmq3-dev git libsqlite3-dev
#download
WORKDIR /
RUN git clone https://github.com/bitcoin/bitcoin.git
#build 
WORKDIR /bitcoin  
RUN git checkout v29.0rc3 && \
    cmake -B build -DSECP256K1_ENABLE_MODULE_MUSIG=ON -DWITH_ZMQ=ON -DBUILD_TESTS=OFF -DCMAKE_CXX_FLAGS="--param ggc-min-expand=1 --param ggc-min-heapsize=32768" && \
    cmake --build build -j 4
 
FROM debian:stable-slim

ARG BITCOIN_VERSION=v29.0rc2
ENV PATH=/opt/bitcoin-${BITCOIN_VERSION}:$PATH

RUN apt-get update -y \
  && apt-get install -y libevent-pthreads-2.1-7 curl gosu libgcc-s1 libstdc++6 libevent-extra-2.1-7 libevent-core-2.1-7 libevent-2.1-7 libboost-system1.74.0 libboost-filesystem1.74.0 libboost-thread1.74.0  libzmq5  libsqlite3-0 \
  && apt-get clean \
  && rm -rf /var/lib/apt/lists/* /tmp/* /var/tmp/*  
#copy from build
COPY --from=build /bitcoin/build/bin/bitcoin-cli /opt/bitcoin-${BITCOIN_VERSION}/
COPY --from=build /bitcoin/build/bin/bitcoind /opt/bitcoin-${BITCOIN_VERSION}/

RUN curl -SLO https://raw.githubusercontent.com/bitcoin/bitcoin/master/contrib/bitcoin-cli.bash-completion \
  && mkdir /etc/bash_completion.d \
  && mv bitcoin-cli.bash-completion /etc/bash_completion.d/ \
  && curl -SLO https://raw.githubusercontent.com/scop/bash-completion/master/bash_completion \
  && mv bash_completion /usr/share/bash-completion/

COPY docker-entrypoint.sh /entrypoint.sh
COPY bashrc /home/bitcoin/.bashrc

RUN chmod a+x /entrypoint.sh

VOLUME ["/home/bitcoin/.bitcoin"]

EXPOSE 18443 18444 28334 28335

ENTRYPOINT ["/entrypoint.sh"]

CMD ["bitcoind"]

 