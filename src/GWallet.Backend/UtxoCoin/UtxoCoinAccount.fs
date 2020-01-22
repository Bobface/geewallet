﻿namespace GWallet.Backend.UtxoCoin

// NOTE: we can rename this file to less redundant "Account.fs" when this F# compiler bug is fixed:
// https://github.com/Microsoft/visualfsharp/issues/3231

open System
open System.Security
open System.Threading
open System.Linq

open NBitcoin

open GWallet.Backend

type internal TransactionOutpoint =
    {
        Transaction: Transaction;
        OutputIndex: int;
    }
    member self.ToCoin (): Coin =
        Coin(self.Transaction, uint32 self.OutputIndex)

type IUtxoAccount =
    inherit IAccount

    abstract member PublicKey: PubKey with get


type NormalUtxoAccount(currency: Currency, accountFile: FileRepresentation,
                       fromAccountFileToPublicAddress: FileRepresentation -> string,
                       fromAccountFileToPublicKey: FileRepresentation -> PubKey) =
    inherit GWallet.Backend.NormalAccount(currency, accountFile, fromAccountFileToPublicAddress)

    interface IUtxoAccount with
        member val PublicKey = fromAccountFileToPublicKey accountFile with get

type ReadOnlyUtxoAccount(currency: Currency, accountFile: FileRepresentation,
                         fromAccountFileToPublicAddress: FileRepresentation -> string,
                         fromAccountFileToPublicKey: FileRepresentation -> PubKey) =
    inherit GWallet.Backend.ReadOnlyAccount(currency, accountFile, fromAccountFileToPublicAddress)

    interface IUtxoAccount with
        member val PublicKey = fromAccountFileToPublicKey accountFile with get

type ArchivedUtxoAccount(currency: Currency, accountFile: FileRepresentation,
                         fromAccountFileToPublicAddress: FileRepresentation -> string,
                         fromAccountFileToPublicKey: FileRepresentation -> PubKey) =
    inherit GWallet.Backend.ArchivedAccount(currency, accountFile, fromAccountFileToPublicAddress)

    interface IUtxoAccount with
        member val PublicKey = fromAccountFileToPublicKey accountFile with get

module Account =

    let private NumberOfParallelJobsForMode mode =
        match mode with
        | ServerSelectionMode.Fast -> 3u
        | ServerSelectionMode.Analysis -> 2u

    let private FaultTolerantParallelClientDefaultSettings (mode: ServerSelectionMode)
                                                           maybeConsistencyConfig =
        let consistencyConfig =
            match maybeConsistencyConfig with
            | None -> SpecificNumberOfConsistentResponsesRequired 2u
            | Some specificConsistencyConfig -> specificConsistencyConfig

        {
            NumberOfParallelJobsAllowed = NumberOfParallelJobsForMode mode
            NumberOfRetries = Config.NUMBER_OF_RETRIES_TO_SAME_SERVERS;
            NumberOfRetriesForInconsistency = Config.NUMBER_OF_RETRIES_TO_SAME_SERVERS;
            ExceptionHandler = Some (fun ex -> Infrastructure.ReportWarning ex)
            ExtraProtectionAgainstUnfoundedCancellations = false
            ResultSelectionMode =
                Selective
                    {
                        ServerSelectionMode = mode
                        ConsistencyConfig = consistencyConfig
                        ReportUncanceledJobs = (not Config.NewUtxoTcpClientDisabled)
                    }
        }

    let private FaultTolerantParallelClientSettingsForBroadcast() =
        FaultTolerantParallelClientDefaultSettings ServerSelectionMode.Fast
                                                   (Some (SpecificNumberOfConsistentResponsesRequired 1u))

    let private FaultTolerantParallelClientSettingsForBalanceCheck (mode: ServerSelectionMode)
                                                                   cacheOrInitialBalanceMatchFunc =
        let consistencyConfig =
            if mode = ServerSelectionMode.Fast then
                Some (OneServerConsistentWithCertainValueOrTwoServers cacheOrInitialBalanceMatchFunc)
            else
                None
        FaultTolerantParallelClientDefaultSettings mode consistencyConfig

    let private faultTolerantElectrumClient =
        FaultTolerantParallelClient<ServerDetails,ServerDiscardedException> Caching.Instance.SaveServerLastStat

    let internal GetNetwork (currency: Currency) =
        if not (currency.IsUtxo()) then
            failwithf "Assertion failed: currency %A should be UTXO-type" currency
        match currency with
        | BTC -> Config.BitcoinNet
        | LTC -> Config.LitecoinNet
        | _ -> failwithf "Assertion failed: UTXO currency %A not supported?" currency

    // technique taken from https://electrumx.readthedocs.io/en/latest/protocol-basics.html#script-hashes
    let private GetElectrumScriptHashFromAddress (address: BitcoinAddress): string =
        let sha = NBitcoin.Crypto.Hashes.SHA256(address.ScriptPubKey.ToBytes())
        let reversedSha = sha.Reverse().ToArray()
        NBitcoin.DataEncoders.Encoders.Hex.EncodeData reversedSha

    let public GetElectrumScriptHashFromPublicAddress currency (publicAddress: string) =
        // TODO: measure how long does it take to get the script hash and if it's too long, cache it at app startup?
        BitcoinAddress.Create(publicAddress, GetNetwork currency) |> GetElectrumScriptHashFromAddress

    let internal GetPublicAddressFromPublicKey currency (publicKey: PubKey) =
        (publicKey.GetSegwitAddress (GetNetwork currency)).GetScriptAddress().ToString()

    let GetPublicAddressFromNormalAccountFile (currency: Currency) (accountFile: FileRepresentation): string =
        let pubKey = PubKey(accountFile.Name)
        GetPublicAddressFromPublicKey currency pubKey

    let GetPublicKeyFromNormalAccountFile (accountFile: FileRepresentation): PubKey =
        PubKey accountFile.Name

    let GetPublicKeyFromReadOnlyAccountFile (accountFile: FileRepresentation): PubKey =
        accountFile.Content() |> PubKey

    let GetPublicAddressFromUnencryptedPrivateKey (currency: Currency) (privateKey: string) =
        let privateKey = Key.Parse(privateKey, GetNetwork currency)
        GetPublicAddressFromPublicKey currency privateKey.PubKey

    // FIXME: seems there's some code duplication between this function and EtherServer.fs's GetServerFuncs function
    //        and room for simplification to not pass a new ad-hoc delegate?
    let GetServerFuncs<'R> (electrumClientFunc: Async<StratumClient>->Async<'R>)
                           (electrumServers: seq<ServerDetails>)
                               : seq<Server<ServerDetails,'R>> =

        let ElectrumServerToRetrievalFunc (server: ServerDetails)
                                          (electrumClientFunc: Async<StratumClient>->Async<'R>)
                                              : Async<'R> = async {
            try
                let stratumClient = ElectrumClient.StratumServer server
                return! electrumClientFunc stratumClient

            // NOTE: try to make this 'with' block be in sync with the one in EtherServer:GetWeb3Funcs()
            with
            | :? CommunicationUnsuccessfulException as ex ->
                let msg = sprintf "%s: %s" (ex.GetType().FullName) ex.Message
                return raise <| ServerDiscardedException(msg, ex)
            | ex ->
                return raise <| Exception(sprintf "Some problem when connecting to %s" server.ServerInfo.NetworkPath, ex)
        }
        let ElectrumServerToGenericServer (electrumClientFunc: Async<StratumClient>->Async<'R>)
                                          (electrumServer: ServerDetails)
                                              : Server<ServerDetails,'R> =
            { Details = electrumServer
              Retrieval = ElectrumServerToRetrievalFunc electrumServer electrumClientFunc }

        let serverFuncs =
            Seq.map (ElectrumServerToGenericServer electrumClientFunc)
                     electrumServers
        serverFuncs

    let private GetRandomizedFuncs<'R> (currency: Currency)
                                       (electrumClientFunc: Async<StratumClient>->Async<'R>)
                                              : List<Server<ServerDetails,'R>> =

        let electrumServers = ElectrumServerSeedList.Randomize currency
        GetServerFuncs electrumClientFunc electrumServers
            |> List.ofSeq

    let private BalanceToShow (balances: BlockchainScripthahsGetBalanceInnerResult) =
        let unconfirmedPlusConfirmed = balances.Unconfirmed + balances.Confirmed
        let amountToShowInSatoshis =
            if unconfirmedPlusConfirmed < balances.Confirmed then
                unconfirmedPlusConfirmed
            else
                balances.Confirmed
        let amountInBtc = (Money.Satoshis amountToShowInSatoshis).ToUnit MoneyUnit.BTC
        amountInBtc

    let private BalanceMatchWithCacheOrInitialBalance address
                                                      currency
                                                      (someRetrievedBalance: BlockchainScripthahsGetBalanceInnerResult)
                                                          : bool =
        if Caching.Instance.FirstRun then
            BalanceToShow someRetrievedBalance = 0m
        else
            match Caching.Instance.TryRetrieveLastCompoundBalance address currency with
            | None -> false
            | Some balance ->
                BalanceToShow someRetrievedBalance = balance

    let private GetBalances (account: IUtxoAccount)
                            (mode: ServerSelectionMode)
                            (cancelSourceOption: Option<CancellationTokenSource>)
                                : Async<BlockchainScripthahsGetBalanceInnerResult> =
        let scriptHashHex = GetElectrumScriptHashFromPublicAddress account.Currency account.PublicAddress

        let query =
            match cancelSourceOption with
            | None ->
                faultTolerantElectrumClient.Query
            | Some cancelSource ->
                faultTolerantElectrumClient.QueryWithCancellation cancelSource
        let balanceJob =
            query
                (FaultTolerantParallelClientSettingsForBalanceCheck
                    mode (BalanceMatchWithCacheOrInitialBalance account.PublicAddress account.Currency))
                (GetRandomizedFuncs account.Currency (ElectrumClient.GetBalance scriptHashHex))
        balanceJob

    let private GetBalancesFromServer (account: IUtxoAccount)
                                      (mode: ServerSelectionMode)
                                      (cancelSourceOption: Option<CancellationTokenSource>)
                                         : Async<Option<BlockchainScripthahsGetBalanceInnerResult>> =
        async {
            try
                let! balances = GetBalances account mode cancelSourceOption
                return Some balances
            with
            | ex when (FSharpUtil.FindException<ResourceUnavailabilityException> ex).IsSome ->
                return None
        }

    let internal GetShowableBalance (account: IUtxoAccount)
                                    (mode: ServerSelectionMode)
                                    (cancelSourceOption: Option<CancellationTokenSource>)
                                        : Async<Option<decimal>> =
        async {
            let! maybeBalances = GetBalancesFromServer account mode cancelSourceOption
            match maybeBalances with
            | Some balances ->
                return Some (BalanceToShow balances)
            | None ->
                return None
        }

    let private ConvertToICoin (account: IUtxoAccount) (inputOutpointInfo: TransactionInputOutpointInfo): ICoin =
        let txHash = uint256 inputOutpointInfo.TransactionHash
        let scriptPubKeyInBytes = NBitcoin.DataEncoders.Encoders.Hex.DecodeData inputOutpointInfo.DestinationInHex
        let scriptPubKey = Script(scriptPubKeyInBytes)
        let coin =
            Coin(txHash, uint32 inputOutpointInfo.OutputIndex, Money(inputOutpointInfo.ValueInSatoshis), scriptPubKey)
        coin.ToScriptCoin(account.PublicKey.WitHash.ScriptPubKey) :> ICoin

    let private CreateTransactionAndCoinsToBeSigned (account: IUtxoAccount)
                                                    (transactionInputs: List<TransactionInputOutpointInfo>)
                                                    (destination: string)
                                                    (amount: TransferAmount)
                                                        : TransactionBuilder =
        let coins = List.map (ConvertToICoin account) transactionInputs

        let transactionBuilder = (GetNetwork account.Currency).CreateTransactionBuilder()
        transactionBuilder.AddCoins coins |> ignore

        let currency = account.Currency
        let destAddress = BitcoinAddress.Create(destination, GetNetwork currency)

        if amount.BalanceAtTheMomentOfSending <> amount.ValueToSend then
            let moneyAmount = Money(amount.ValueToSend, MoneyUnit.BTC)
            transactionBuilder.Send(destAddress, moneyAmount) |> ignore
            let originAddress = (account :> IAccount).PublicAddress
            let changeAddress = BitcoinAddress.Create(originAddress, GetNetwork currency)
            transactionBuilder.SetChange changeAddress |> ignore
        else
            transactionBuilder.SendAll destAddress |> ignore

        // to enable RBF, see https://bitcoin.stackexchange.com/a/61038/2751
        // FIXME: use the new API for this in NBitcoin 4.1.2.7 (see https://github.com/MetacoSA/NBitcoin/commit/67e00b00865271a029cd1e21fc2002a2d9f32fcd )
        transactionBuilder.SetLockTime (LockTime 0) |> ignore

        transactionBuilder

    type internal UnspentTransactionOutputInfo =
        {
            TransactionId: string;
            OutputIndex: int;
            Value: Int64;
        }

    let private ConvertToInputOutpointInfo currency (utxo: UnspentTransactionOutputInfo)
                                               : Async<TransactionInputOutpointInfo> =
        async {
            let job = ElectrumClient.GetBlockchainTransaction utxo.TransactionId
            let! transRaw =
                faultTolerantElectrumClient.Query
                    (FaultTolerantParallelClientDefaultSettings ServerSelectionMode.Fast None)
                    (GetRandomizedFuncs currency job)
            let transaction = Transaction.Parse(transRaw, GetNetwork currency)
            let txOut = transaction.Outputs.[utxo.OutputIndex]
            // should suggest a ToHex() method to NBitcoin's TxOut type?
            let valueInSatoshis = txOut.Value
            let destination = txOut.ScriptPubKey.ToHex()
            let ret = {
                TransactionHash = transaction.GetHash().ToString();
                OutputIndex = utxo.OutputIndex;
                ValueInSatoshis = txOut.Value.Satoshi;
                DestinationInHex = destination;
            }
            return ret
        }

    let rec private EstimateFees (txBuilder: TransactionBuilder)
                                 (feeRate: FeeRate)
                                 (account: IUtxoAccount)
                                 (unusedUtxos: List<UnspentTransactionOutputInfo>)
                                     : Async<Money> =
        async {
            try
                let fees = txBuilder.EstimateFees feeRate
                return fees
            with
            | :? NBitcoin.NotEnoughFundsException as ex ->
                match unusedUtxos with
                | [] -> return raise <| FSharpUtil.ReRaise ex
                | head::tail ->
                    let! newInput = head |> ConvertToInputOutpointInfo account.Currency
                    let newCoin = newInput |> ConvertToICoin account
                    let newTxBuilder = txBuilder.AddCoins [newCoin]
                    return! EstimateFees newTxBuilder feeRate account tail
        }

    let EstimateFee (account: IUtxoAccount) (amount: TransferAmount) (destination: string)
                        : Async<TransactionMetadata> = async {
        let rec addInputsUntilAmount (utxos: List<UnspentTransactionOutputInfo>)
                                      soFarInSatoshis
                                      amount
                                     (acc: List<UnspentTransactionOutputInfo>)
                                         : List<UnspentTransactionOutputInfo>*int64*List<UnspentTransactionOutputInfo> =
            match utxos with
            | [] ->
                // should `raise InsufficientFunds` instead?
                failwithf "Not enough funds (needed: %s, got so far: %s)"
                          (amount.ToString()) (soFarInSatoshis.ToString())
            | utxoInfo::tail ->
                let newAcc = utxoInfo::acc

                let newSoFar = soFarInSatoshis + utxoInfo.Value
                if (newSoFar < amount) then
                    addInputsUntilAmount tail newSoFar amount newAcc
                else
                    newAcc,newSoFar,tail

        let job = GetElectrumScriptHashFromPublicAddress account.Currency account.PublicAddress
                  |> ElectrumClient.GetUnspentTransactionOutputs
        let! utxos =
            faultTolerantElectrumClient.Query
                (FaultTolerantParallelClientDefaultSettings ServerSelectionMode.Fast None)
                (GetRandomizedFuncs account.Currency job)

        if not (utxos.Any()) then
            failwith "No UTXOs found!"
        let possibleInputs =
            seq {
                for utxo in utxos do
                    yield { TransactionId = utxo.TxHash; OutputIndex = utxo.TxPos; Value = utxo.Value }
            }

        // first ones are the smallest ones
        let inputsOrderedByAmount = possibleInputs.OrderBy(fun utxo -> utxo.Value) |> List.ofSeq

        let amountInSatoshis = Money(amount.ValueToSend, MoneyUnit.BTC).Satoshi
        let utxosToUse,totalValueOfInputs,unusedInputs =
            addInputsUntilAmount inputsOrderedByAmount 0L amountInSatoshis List.Empty

        let asyncInputs = List.map (ConvertToInputOutpointInfo account.Currency) utxosToUse
        let! inputs = Async.Parallel asyncInputs

        let transactionDraftInputs = inputs |> List.ofArray

        let averageFee (feesFromDifferentServers: List<decimal>): decimal =
            let avg = feesFromDifferentServers.Sum() / decimal feesFromDifferentServers.Length
            avg

        let minResponsesRequired = 3u

        //querying for 1 will always return -1 surprisingly...
        let estimateFeeJob = ElectrumClient.EstimateFee 2

        let! btcPerKiloByteForFastTrans =
            faultTolerantElectrumClient.Query
                (FaultTolerantParallelClientDefaultSettings
                    ServerSelectionMode.Fast
                    (Some (AverageBetweenResponses (minResponsesRequired, averageFee))))

                (GetRandomizedFuncs account.Currency estimateFeeJob)

        let feeRate =
            try
                Money(btcPerKiloByteForFastTrans, MoneyUnit.BTC) |> FeeRate
            with
            | ex ->
                // we need more info in case this bug shows again: https://gitlab.com/DiginexGlobal/geewallet/issues/43
                raise <| Exception(sprintf "Could not create fee rate from %s btc per KB"
                                           (btcPerKiloByteForFastTrans.ToString()), ex)

        let transactionBuilder = CreateTransactionAndCoinsToBeSigned account
                                                                     transactionDraftInputs
                                                                     destination
                                                                     amount
        let! estimatedMinerFee = EstimateFees transactionBuilder feeRate account unusedInputs

        let estimatedMinerFeeInSatoshis = estimatedMinerFee.Satoshi
        let minerFee = MinerFee(estimatedMinerFeeInSatoshis, DateTime.UtcNow, account.Currency)

        return { Inputs = transactionDraftInputs; Fee = minerFee }
    }

    let private SignTransactionWithPrivateKey (account: IUtxoAccount)
                                              (txMetadata: TransactionMetadata)
                                              (destination: string)
                                              (amount: TransferAmount)
                                              (privateKey: Key) =

        let btcMinerFee = txMetadata.Fee
        let amountInSatoshis = Money(amount.ValueToSend, MoneyUnit.BTC).Satoshi

        let finalTransactionBuilder = CreateTransactionAndCoinsToBeSigned account txMetadata.Inputs destination amount

        finalTransactionBuilder.AddKeys privateKey |> ignore
        finalTransactionBuilder.SendFees (Money.Satoshis(btcMinerFee.EstimatedFeeInSatoshis)) |> ignore

        let finalTransaction = finalTransactionBuilder.BuildTransaction true
        let transCheckResultAfterSigning = finalTransaction.Check()
        if (transCheckResultAfterSigning <> TransactionCheckResult.Success) then
            failwithf "Transaction check failed after signing with %A" transCheckResultAfterSigning

        if not (finalTransactionBuilder.Verify finalTransaction) then
            failwith "Something went wrong when verifying transaction"
        finalTransaction

    let internal GetPrivateKey (account: NormalAccount) password =
        let encryptedPrivateKey = account.GetEncryptedPrivateKey()
        let encryptedSecret = BitcoinEncryptedSecretNoEC(encryptedPrivateKey, GetNetwork (account:>IAccount).Currency)
        try
            encryptedSecret.GetKey(password)
        with
        | :? SecurityException ->
            raise (InvalidPassword)

    let SignTransaction (account: NormalUtxoAccount)
                        (txMetadata: TransactionMetadata)
                        (destination: string)
                        (amount: TransferAmount)
                        (password: string) =

        let privateKey = GetPrivateKey account password

        let signedTransaction = SignTransactionWithPrivateKey
                                    account
                                    txMetadata
                                    destination
                                    amount
                                    privateKey
        let rawTransaction = signedTransaction.ToHex()
        rawTransaction

    let CheckValidPassword (account: NormalAccount) (password: string) =
        GetPrivateKey account password |> ignore

    let private BroadcastRawTransaction currency (rawTx: string): Async<string> =
        let job = ElectrumClient.BroadcastTransaction rawTx
        let newTxIdJob =
            faultTolerantElectrumClient.Query
                (FaultTolerantParallelClientSettingsForBroadcast ())
                (GetRandomizedFuncs currency job)
        newTxIdJob

    let BroadcastTransaction currency (transaction: SignedTransaction<_>) =
        // FIXME: stop embedding TransactionInfo element in SignedTransaction<BTC>
        // and show the info from the RawTx, using NBitcoin to extract it
        BroadcastRawTransaction currency transaction.RawTransaction

    let SendPayment (account: NormalUtxoAccount)
                    (txMetadata: TransactionMetadata)
                    (destination: string)
                    (amount: TransferAmount)
                    (password: string)
                    =
        let baseAccount = account :> IAccount
        if (baseAccount.PublicAddress.Equals(destination, StringComparison.InvariantCultureIgnoreCase)) then
            raise DestinationEqualToOrigin

        let finalTransaction = SignTransaction account txMetadata destination amount password
        BroadcastRawTransaction baseAccount.Currency finalTransaction

    // TODO: maybe move this func to Backend.Account module, or simply inline it (simple enough)
    let public ExportUnsignedTransactionToJson trans =
        Marshalling.Serialize trans

    let SaveUnsignedTransaction (transProposal: UnsignedTransactionProposal)
                                (txMetadata: TransactionMetadata)
                                (readOnlyAccounts: seq<ReadOnlyAccount>)
                                    : string =

        let unsignedTransaction =
            {
                Proposal = transProposal;
                Cache = Caching.Instance.GetLastCachedData().ToDietCache readOnlyAccounts;
                Metadata = txMetadata;
            }
        ExportUnsignedTransactionToJson unsignedTransaction

    let SweepArchivedFunds (account: ArchivedUtxoAccount)
                           (balance: decimal)
                           (destination: IAccount)
                           (txMetadata: TransactionMetadata) =
        let currency = (account:>IAccount).Currency
        let network = GetNetwork currency
        let amount = TransferAmount(balance, balance, currency)
        let privateKey = Key.Parse(account.GetUnencryptedPrivateKey(), network)
        let signedTrans = SignTransactionWithPrivateKey
                              account txMetadata destination.PublicAddress amount privateKey
        BroadcastRawTransaction currency (signedTrans.ToHex())

    let Create currency (password: string) (seed: array<byte>): Async<FileRepresentation> =
        async {
            let privKey = Key seed
            let network = GetNetwork currency
            let secret = privKey.GetBitcoinSecret network
            let encryptedSecret = secret.PrivateKey.GetEncryptedBitcoinSecret(password, network)
            let encryptedPrivateKey = encryptedSecret.ToWif()
            let publicKey = secret.PubKey.ToString()
            return {
                Name = publicKey
                Content = fun _ -> encryptedPrivateKey
            }
        }

    let ValidateAddress (currency: Currency) (address: string) =
        if String.IsNullOrEmpty address then
            raise <| ArgumentNullException "address"

        let BITCOIN_ADDRESS_BECH32_PREFIX = "bc1"

        let utxoCoinValidAddressPrefixes =
            match currency with
            | BTC ->
                let BITCOIN_ADDRESS_PUBKEYHASH_PREFIX = "1"
                let BITCOIN_ADDRESS_SCRIPTHASH_PREFIX = "3"
                [
                    BITCOIN_ADDRESS_PUBKEYHASH_PREFIX
                    BITCOIN_ADDRESS_SCRIPTHASH_PREFIX
                    BITCOIN_ADDRESS_BECH32_PREFIX
                ]
            | LTC ->
                let LITECOIN_ADDRESS_PUBKEYHASH_PREFIX = "L"
                let LITECOIN_ADDRESS_SCRIPTHASH_PREFIX = "M"
                [ LITECOIN_ADDRESS_PUBKEYHASH_PREFIX; LITECOIN_ADDRESS_SCRIPTHASH_PREFIX ]
            | _ -> failwithf "Unknown UTXO currency %A" currency

        if not (utxoCoinValidAddressPrefixes.Any(fun prefix -> address.StartsWith prefix)) then
            raise (AddressMissingProperPrefix(utxoCoinValidAddressPrefixes))

        let minLength,lenghtInBetweenAllowed,maxLength =
            if currency = Currency.BTC && (address.StartsWith BITCOIN_ADDRESS_BECH32_PREFIX) then
                // taken from https://github.com/bitcoin/bips/blob/master/bip-0173.mediawiki
                // (FIXME: this is only valid for the first version of segwit, fix it!)
                42,false,62
            else
                27,true,34
        let limits = [ minLength; maxLength ]
        if address.Length > maxLength then
            raise <| AddressWithInvalidLength limits
        if address.Length < minLength then
            raise <| AddressWithInvalidLength limits
        if not lenghtInBetweenAllowed && (address.Length <> minLength && address.Length <> maxLength) then
            raise <| AddressWithInvalidLength limits

        let network = GetNetwork currency
        try
            BitcoinAddress.Create(address, network) |> ignore
        with
        // TODO: propose to NBitcoin upstream to generate an NBitcoin exception instead
        | :? FormatException ->
            raise (AddressWithInvalidChecksum None)
