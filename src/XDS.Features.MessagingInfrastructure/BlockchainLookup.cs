﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Blockcore.Configuration;
using Blockcore.Consensus;
using Blockcore.EventBus;
using Blockcore.EventBus.CoreEvents;
using Blockcore.Interfaces;
using Blockcore.Signals;
using Blockcore.Utilities;
using Microsoft.Extensions.Logging;
using NBitcoin;
using XDS.Features.MessagingInfrastructure.Addresses;
using XDS.Features.MessagingInfrastructure.Balances;
using XDS.Features.MessagingInfrastructure.Blockchain;
using XDS.Features.MessagingInfrastructure.Model;
using XDS.Features.MessagingInfrastructure.PhotonServices;
using XDS.Features.MessagingInfrastructure.Tools;
using XDS.SDK.Messaging.CrossTierTypes.Photon;

namespace XDS.Features.MessagingInfrastructure
{
    public class BlockchainLookup
    {
        readonly SemaphoreSlim WalletSemaphore = new SemaphoreSlim(1, 1);
        readonly ILogger logger;
        readonly ChainIndexer chainIndexer;
        readonly IBlockStore blockStore;

        readonly XDSAddressIndex addressIndex;
        readonly AddressService addressService;
        readonly IInitialBlockDownloadState initialBlockDownloadState;
        readonly Network network;
        readonly INodeLifetime nodeLifetime;

        readonly ISignals signals;
        readonly IndexFileHelper indexFileHelper;

        

        public bool IsStartingUp = true;
        private SubscriptionToken blockConnectedSubscription;


        public BlockchainLookup(ILoggerFactory loggerFactory, ChainIndexer chainIndexer, IBlockStore blockStore, ISignals signals, IndexFileHelper indexFileHelper, IInitialBlockDownloadState initialBlockDownloadState, Network network, INodeLifetime nodeLifetime, NodeSettings nodeSettings)
        {
            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);
            this.chainIndexer = chainIndexer;
            this.blockStore = blockStore;
            this.nodeLifetime = nodeLifetime;
            this.signals = signals;
            this.network = network;
            this.initialBlockDownloadState = initialBlockDownloadState;

            this.indexFileHelper = indexFileHelper;
            AddressHelper.Init(network, loggerFactory);
            IndexBalanceService.Init(network);

            Tools.Extensions.Init(loggerFactory);
            this.addressIndex = indexFileHelper.LoadIndex();
            this.addressService = new AddressService(this.addressIndex, indexFileHelper, loggerFactory);


            Task.Run(() =>
            {
                Task.Delay(5000).Wait();
                while (IsIBD())
                {
                    Task.Delay(2000).Wait();
                }

                while (this.chainIndexer.Tip == null || this.chainIndexer.Tip.HashBlock == null)
                {
                    Task.Delay(2000).Wait();
                }
                SyncWallet();
            });
        }

        public int GetAddressCount()
        {
            return this.addressIndex.Entries.Count;
        }

        bool IsIBD()
        {
            try
            {
                return this.initialBlockDownloadState.IsInitialBlockDownload();
            }
            catch (Exception e)
            {
                this.logger.LogWarning(
                    $"Error in {nameof(this.initialBlockDownloadState.IsInitialBlockDownload)}: {e.Message}");
                return true;
            }
        }

        public long GetNetworkBalance()
        {
            List<string> allAddresses = this.addressIndex.Entries.Select(x => x.Address).ToList();

            long networkBalance = 0;
            foreach (string address in allAddresses)
            {
                // occasionally, ab == null - when does that happen? Investigate!
                IndexAddressBalance ab = IndexBalanceService.GetBalance(this.addressIndex.Entries, this.GetSyncedHeight(), address);
                networkBalance += ab.Confirmed;
            }

            return networkBalance / Constants.SatoshisPerCoin;
        }

        /// <summary>
        /// Check if the wallet tip hash is in the current consensus chain.
        /// </summary>
        bool IsOnBestChain()
        {
            bool isOnBestChain;
            if (this.addressIndex.SyncedHeight == 0 || this.addressIndex.SyncedHash.IsDefaultBlockHash(this.network.GenesisHash.ToBytes()))
            {
                // If the height is 0, we cannot be on the wrong chain. Reset file in case there is something wrong with it.
                ResetMetadata();
                isOnBestChain = true;
                this.logger.LogInformation($"IsOnBestChain: Yes - wallet height is 0.");

            }
            else
            {
                var walletTipHash = new uint256(this.addressIndex.SyncedHash.Value);

                var chainedHeader = this.chainIndexer.GetHeader(walletTipHash);
                isOnBestChain = chainedHeader != null;
                if (isOnBestChain)
                {
                    this.logger.LogInformation($"IsOnBestChain: Yes - because wallet tip hash {walletTipHash} is chainedHeader {chainedHeader.Height}.");
                }
                else
                {
                    this.logger.LogInformation($"IsOnBestChain: No - because wallet tip hash {walletTipHash} is unknown to chainIndexer.");
                }
            }

            return isOnBestChain;
        }
        /// <summary>
        /// Clears and initializes the wallet Metadata file, and sets heights to 0 and the hashes to null,
        /// and saves the Metadata file, effectively updating it to the latest version.
        /// </summary>
        void ResetMetadata()
        {
            this.addressIndex.SyncedHash = this.network.GenesisHash.ToHash256();
            this.addressIndex.SyncedHeight = 0;
            this.addressIndex.CheckpointHash = this.addressIndex.SyncedHash;
            this.addressIndex.CheckpointHeight = 0;
            this.addressIndex.IndexIdentifier = this.addressIndex.IndexIdentifier;
            this.addressIndex.Entries.Clear();
            this.logger.LogInformation($"Resetting blockIndex to initial state, forcing a save.");
            SaveMetadata(this.addressIndex.SyncedHeight, this.addressIndex.SyncedHash, force: true);
        }
        void MoveToBestChain()
        {
            ChainedHeader checkpointHeader = null;
            if (!this.addressIndex.CheckpointHash.IsDefaultBlockHash(this.network.GenesisHash.ToBytes()))
            {
                var header = this.chainIndexer.GetHeader(new uint256(this.addressIndex.CheckpointHash.Value));
                if (header != null && this.addressIndex.CheckpointHeight == header.Height)
                    checkpointHeader = header;  // the checkpoint header is in the correct chain and the the checkpoint height in the wallet is consistent
            }

            if (checkpointHeader != null && this.chainIndexer.Tip.Height - checkpointHeader.Height > this.network.Consensus.MaxReorgLength)  // also check the checkpoint is not newer than it should be
            {
                // we have a valid checkpoint, remove all later blocks
                RemoveBlocks(checkpointHeader);
            }
            else
            {
                // we do not have a usable checkpoint, sync from start by resetting everything
                ResetMetadata();
            }
        }

        /// <summary>
        /// It is assumed that the argument contains the header of the highest block (inclusive) where the wallet data is
        /// consistent with the right chain.
        /// This method removes all block and the transactions in them of later blocks.
        /// </summary>
        /// <param name="checkpointHeader">ChainedHeader of the checkpoint</param>
        void RemoveBlocks(ChainedHeader checkpointHeader)
        {
            var entries = this.addressIndex.Entries.ToList();
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                List<IndexUtxo> deleteReceived = new List<IndexUtxo>();
                foreach (var utxo in entry.Received)
                {
                    if (utxo.BlockHeight > checkpointHeader.Height)
                        deleteReceived.Add(utxo);
                    else
                    {
                        if (utxo.SpendingHeight > checkpointHeader.Height)
                        {
                            utxo.SpendingTx = null;
                            utxo.SpendingHeight = 0;
                            utxo.SpendingN = 0;
                        }
                    }
                }

                foreach (var utxo in deleteReceived)
                {
                    entry.Received.Remove(utxo);
                }

                if (entry.Received.Count == 0)
                    this.addressIndex.Entries.Remove(entry);
            }



            // Update last block synced height
            this.addressIndex.SyncedHeight = checkpointHeader.Height;
            this.addressIndex.SyncedHash = checkpointHeader.HashBlock.ToHash256();
            this.addressIndex.CheckpointHeight = checkpointHeader.Height;
            this.addressIndex.CheckpointHash = checkpointHeader.HashBlock.ToHash256();
            SaveMetadata(this.addressIndex.SyncedHeight, this.addressIndex.SyncedHash, force: true);
        }

        void CompleteStart()
        {
            this.IsStartingUp = false;
            this.logger.LogInformation($"Startup sync completed at height {this.addressIndex.SyncedHeight}, forcing a save.");
            SaveMetadata(this.addressIndex.SyncedHeight, this.addressIndex.SyncedHash, true);
            SubscribeSignals();
        }
        void SubscribeSignals()
        {
            this.blockConnectedSubscription = this.signals.Subscribe<BlockConnected>(OnBlockConnected);
        }

        void UnSubscribeSignals()
        {
            if (this.blockConnectedSubscription != null)
                this.signals.Unsubscribe(this.blockConnectedSubscription);
        }


        void OnBlockConnected(BlockConnected blockConnected)
        {
            if (blockConnected.ConnectedBlock.ChainedHeader.Height <= GetSyncedHeight())
            {
                this.logger.LogWarning($"OnBlockConnected is passing block of height {blockConnected.ConnectedBlock.ChainedHeader.Height}, but the index is already at height {GetSyncedHeight()}. Skipping block!");
                return;
            }

            SyncWallet();
        }

        public int GetSyncedHeight()
        {
            return this.addressIndex.SyncedHeight;
        }

        protected Hash256 GetSyncedHash()
        {
            return this.addressIndex.SyncedHash;
        }




        void SaveMetadata(int height, Hash256 hashBlock, bool force)
        {
            UpdateLastBlockSyncedAndCheckpoint(height, hashBlock);
            this.indexFileHelper.SaveIndex(this.addressIndex, force);

            void UpdateLastBlockSyncedAndCheckpoint(int height, Hash256 hashBlock)
            {
                this.addressIndex.SyncedHeight = height;
                this.addressIndex.SyncedHash = hashBlock;

                const int minCheckpointHeight = 125;
                if (height > minCheckpointHeight)
                {
                    var checkPoint = this.chainIndexer.GetHeader(height - minCheckpointHeight);
                    this.addressIndex.CheckpointHash = checkPoint.HashBlock.ToHash256();
                    this.addressIndex.CheckpointHeight = checkPoint.Height;
                }
                else
                {
                    this.addressIndex.CheckpointHash = this.network.GenesisHash.ToHash256();
                    this.addressIndex.CheckpointHeight = 0;
                }
            }
        }

        protected void SyncWallet()
        {
            this.logger.LogInformation($"Wallet is syncing {(this.IsStartingUp ? "(startup)." : "(updating).")}");
            try
            {
                // a) check if the wallet is on the right chain
                if (!IsOnBestChain())
                {
                    MoveToBestChain();
                }

                while (this.chainIndexer.Tip.Height > this.addressIndex.SyncedHeight)
                {
                    // this can take a long time, so watch for cancellation
                    if (this.nodeLifetime.ApplicationStopping.IsCancellationRequested)
                    {
                        return;
                    }

                    var nextBlockHeight = this.addressIndex.SyncedHeight + 1;

                    ChainedHeader nextBlockHeader = this.chainIndexer.GetHeader(nextBlockHeight);
                    Block nextBlock = this.blockStore.GetBlock(nextBlockHeader.HashBlock);
                    if (nextBlock == null)
                    {
                        this.logger.LogWarning($"Block with hash {nextBlockHeader.HashBlock}, height {nextBlockHeight} is not in the BlockStore.");
                        Task.Delay(2000).Wait();
                        continue;

                    }

                    lock (this.lockObj)
                    {
                        ProcessBlock(nextBlock, nextBlockHeader.Height, nextBlock.GetHash256());
                        //var networkBalance = GetNetworkBalance();
                        //var expectedBalance = nextBlockHeader.Height * 50;
                        //if (networkBalance != expectedBalance)
                        //    ;
                    }

                }
            }
            catch (Exception e)
            {
                this.logger.LogError($"{nameof(SyncWallet)}: {e.Message}");
            }

            if (this.IsStartingUp)
                CompleteStart();
        }

        public object lockObj = new object();

        public long ProcessBlockMS { get; private set; }
        Stopwatch stopwatchProcessBlock = new Stopwatch();

        void ProcessBlock(Block block, int height, Hash256 hashBlock)
        {
            if (this.addressIndex.SyncedHeight != height - 1 || this.addressIndex.SyncedHash == hashBlock)
            {
                this.IsStartingUp = true;
                UnSubscribeSignals();
                MoveToBestChain();
                SyncWallet();
            }

            this.stopwatchProcessBlock.Restart();

            BlockService.AnalyzeBlock(block, height, this.addressService.GetOrCreateAddressInIndex, this.addressService.FindUtxo);

            this.ProcessBlockMS = this.stopwatchProcessBlock.ElapsedMilliseconds;

            SaveMetadata(height, hashBlock, force: false);
        }

        internal (long balance, int height, byte[] hashBlock, PhotonError photonError) GetBalanceFromIndex(string address, PhotonFlags photonFlags, out IndexAddressBalance balance)
        {
            balance = null;
            var entry = this.addressService.FindAddressInIndex(address);

            if (entry == null)
                return (default, default, default, PhotonError.UnknownAddress);

            if(this.IsStartingUp)
                return (default, default, default, PhotonError.ServiceInitializing);

            // todo: make this a transaction
            var height = this.GetSyncedHeight();
            var hash = this.GetSyncedHash();
            balance = IndexBalanceService.GetBalance(this.addressIndex.Entries, height, address);

            switch (photonFlags)
            {
                case PhotonFlags.Confirmed:
                    return (balance.Confirmed, height, hash.Value, PhotonError.Success);
                case PhotonFlags.Spendable:
                    return (balance.Spendable, height, hash.Value, PhotonError.Success);
                case PhotonFlags.Staking:
                    return (balance.Stakable, height, hash.Value, PhotonError.Success);
                default:
                    return (default, default, default, PhotonError.InvalidArguments);
            }
           
        }

        internal (long balance, int height, byte[] hashBlock, IPhotonOutput[] outputs, PhotonError photonError) GetOutputsFromIndex(string address, PhotonFlags photonFlags)
        {
            var result = this.GetBalanceFromIndex(address, photonFlags, out var balance);
           
            if (result.photonError != PhotonError.Success)
                return (default, default, default, default, result.photonError);

           
            IndexUtxo[] indexUtxos = null;

            switch (photonFlags)
            {
                case PhotonFlags.Confirmed:
                    // this is basically a history query
                    bool includeSpent = photonFlags.HasFlag(PhotonFlags.IncludeSpentOutputs);
                    return (default, default, default, default, PhotonError.NotImplemented);
                case PhotonFlags.Spendable:
                   indexUtxos = balance.SpendableCoins.Values.ToArray();
                    break;
                case PhotonFlags.Staking:
                    indexUtxos = balance.StakingCoins.Values.ToArray();
                    break;
              
                default:
                    return (default, default, default,default, PhotonError.InvalidArguments);
            }

            IPhotonOutput[] outputs = new IPhotonOutput[indexUtxos.Length];
            for (var i= 0; i<indexUtxos.Length; i++)
            {
                var utxo = indexUtxos[i];

                outputs[i] = new PhotonOutput
                {
                    SpendingTx = utxo.SpendingTx?.Value,
                    HashTx = utxo.HashTx.Value,
                    SpendingHeight = utxo.SpendingHeight,
                    SpendingN = utxo.SpendingN,
                    BlockHeight = utxo.BlockHeight,
                    Index = utxo.Index,
                    Satoshis = utxo.Satoshis,
                    UtxoType = utxo.UtxoType
                };
            }

            return (result.balance, result.height, result.hashBlock, outputs, PhotonError.Success);
        }
    }
}
