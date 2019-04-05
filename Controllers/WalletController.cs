﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using System.Numerics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using viafront3.Models;
using viafront3.Models.WalletViewModels;
using viafront3.Services;
using viafront3.Data;
using via_jsonrpc;
using xchwallet;

namespace viafront3.Controllers
{
    [Authorize(Roles = Utils.EmailConfirmedRole)]
    [Route("[controller]/[action]")]
    public class WalletController : BaseSettingsController
    {
        private readonly ILogger _logger;
        private readonly IWalletProvider _walletProvider;
        private readonly IEmailSender _emailSender;
        private readonly KycSettings _kycSettings;

        public WalletController(
          UserManager<ApplicationUser> userManager,
          ILogger<ManageController> logger,
          ApplicationDbContext context,
          IOptions<ExchangeSettings> settings,
          IWalletProvider walletProvider,
          IEmailSender emailSender,
          IOptions<KycSettings> kycSettings) : base(userManager, context, settings)
        {
            _logger = logger;
            _walletProvider = walletProvider;
            _emailSender = emailSender;
            _kycSettings = kycSettings.Value;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var user = await GetUser(required: true);

            //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
            var via = new ViaJsonRpc(_settings.AccessHttpUrl);
            var balances = via.BalanceQuery(user.Exchange.Id);

            var model = new BalanceViewModel
            {
                User = user,
                AssetSettings = _settings.Assets,
                Balances = balances
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Deposits()
        {
            var user = await GetUser(required: true);
            var model = new BaseViewModel
            {
                User = user
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Deposit(string asset)
        {
            var user = await GetUser(required: true);

            if (_walletProvider.IsFiat(asset))
                return RedirectToAction("DepositFiat", new {asset=asset});

            var wallet = _walletProvider.GetChain(asset);
            var addrs = wallet.GetAddresses(user.Id);
            WalletAddr addr = null;
            if (addrs.Any())
                addr = addrs.First();
            else
            {
                if (!wallet.HasTag(user.Id))
                {
                    wallet.NewTag(user.Id);
                    wallet.Save();
                }
                addr = wallet.NewAddress(user.Id);
                wallet.Save();
            }

            var model = new DepositViewModel
            {
                User = user,
                Asset = asset,
                DepositAddress = addr.Address,
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> TransactionCheck(string asset, string address)
        {
            var user = await GetUser(required: true);

            // get wallet address
            var wallet = _walletProvider.GetChain(asset);
            var addrs = wallet.GetAddresses(user.Id);
            WalletAddr addr = null;
            if (addrs.Any())
                addr = addrs.First();
            else
                addr = wallet.NewAddress(user.Id);

            // update wallet from blockchain
            wallet.UpdateFromBlockchain();
            wallet.Save();

            var chainAssetSettings = _walletProvider.ChainAssetSettings(asset);
            var addrTxs = await Utils.CheckAddressIncommingTxsAndUpdateWalletAndExchangeBalance(_emailSender, _settings, asset, wallet, chainAssetSettings, user, addr);
            var newDepositsHuman = wallet.AmountToString(addrTxs.NewDeposits);

            var model = new TransactionCheckViewModel
            {
                User = user,
                Asset = asset,
                AssetSettings = _settings.Assets[asset],
                ChainAssetSettings = chainAssetSettings,
                DepositAddress = addr.Address,
                Wallet = wallet,
                TransactionsIncomming = addrTxs.IncommingTxs,
                NewTransactionsIncomming = addrTxs.JustAckedTxs,
                NewDeposits = addrTxs.NewDeposits,
                NewDepositsHuman = newDepositsHuman,
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> DepositFiat(string asset)
        {
            var user = await GetUser(required: true);

            var model = new DepositFiatViewModel
            {
                User = user,
                Asset = asset,
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DepositFiat(DepositFiatViewModel model)
        {
            var user = await GetUser(required: true);

            var wallet = _walletProvider.GetFiat(model.Asset);
            var amount = wallet.StringToAmount(model.Amount.ToString());
            if (amount <= 0)
            {
                this.FlashError("Amount must be greater then 0");
                return View(model);
            }
            var decimals = _settings.Assets[model.Asset].Decimals;
            if (Utils.GetDecimalPlaces(model.Amount) > decimals)
            {
                this.FlashError($"Amount must have a maximum of {decimals} digits after the decimal place");
                return View(model);
            }
            if (!wallet.HasTag(user.Id))
            {
                wallet.NewTag(user.Id);
                wallet.Save();
            }
            var tx = wallet.RegisterPendingDeposit(user.Id, amount);
            model.PendingTx = tx;
            model.Account = wallet.GetAccount();
            wallet.Save();

            // send email: deposit created
            await _emailSender.SendEmailFiatDepositCreatedAsync(user.Email, model.Asset, wallet.AmountToString(tx.Amount), tx.DepositCode, wallet.GetAccount());

            return View("DepositFiatCreated", model);
        }

        [HttpGet]
        public async Task<IActionResult> FiatTransactionView(string asset)
        {
            var user = await GetUser(required: true);

            // get wallet address
            var wallet = _walletProvider.GetFiat(asset);
            var txs = wallet.GetTransactions(user.Id);
           
            var model = new FiatTransactionsViewModel
            {
                User = user,
                Asset = asset,
                AssetSettings = _settings.Assets,
                Wallet = wallet,
                Transactions = txs,
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Withdrawals()
        {
            var user = await GetUser(required: true);
            var model = new BaseViewModel
            {
                User = user
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Withdraw(string asset)
        {
            var user = await GetUser(required: true);

            if (_walletProvider.IsFiat(asset))
                return RedirectToAction("WithdrawFiat", new {asset=asset});

            //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
            var via = new ViaJsonRpc(_settings.AccessHttpUrl);
            var balance = via.BalanceQuery(user.Exchange.Id, asset);

            var model = new WithdrawViewModel
            {
                User = user,
                Asset = asset,
                BalanceAvailable = balance.Available,
            };

            return View(model);
        }

        public decimal CalculateWithdrawalAssetEquivalent(ILogger logger, ExchangeSettings settings, KycSettings kyc, string asset, decimal amount)
        {
            if (asset == kyc.WithdrawalAsset)
                return amount;

            foreach (var market in settings.Markets.Keys)
            {
                if (market.StartsWith(asset) && market.EndsWith(kyc.WithdrawalAsset))
                {
                    //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                    var via = new ViaJsonRpc(_settings.AccessHttpUrl);
                    var price = via.MarketPriceQuery(market);
                    return amount * decimal.Parse(price);
                }
            };
            logger.LogError($"no price found for asset {asset}");
            throw new Exception($"no price found for asset {asset}");
        }

        public Tuple<bool, decimal, string> ValidateWithdrawlLimit(ApplicationUser user, string asset, decimal amount)
        {
            var withdrawalTotalThisPeriod = user.WithdrawalTotalThisPeriod(_kycSettings);
            var withdrawalAssetAmount = CalculateWithdrawalAssetEquivalent(_logger, _settings, _kycSettings, asset, amount);
            var newWithdrawalTotal = withdrawalTotalThisPeriod + withdrawalAssetAmount;
            var kycLevel = _kycSettings.Levels[0];
            if (user.Kyc != null && user.Kyc.Level < _kycSettings.Levels.Count)
                kycLevel = _kycSettings.Levels[user.Kyc.Level];
            if (decimal.Parse(kycLevel.WithdrawalLimit) <= newWithdrawalTotal)
                return new Tuple<bool, decimal, string>(false, 0,
                    $"Your withdrawal limit is {kycLevel.WithdrawalLimit} {_kycSettings.WithdrawalAsset} equivalent, your current withdrawal total this period ({_kycSettings.WithdrawalPeriod}) is {withdrawalTotalThisPeriod} {_kycSettings.WithdrawalAsset}");

            return new Tuple<bool, decimal, string>(true, withdrawalAssetAmount, null);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(WithdrawViewModel model)
        {
            var user = await GetUser(required: true);

            //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
            var via = new ViaJsonRpc(_settings.AccessHttpUrl);
            var balance = via.BalanceQuery(user.Exchange.Id, model.Asset);
            model.BalanceAvailable = balance.Available;

            if (ModelState.IsValid)
            {
                var wallet = _walletProvider.GetChain(model.Asset);

                // validate amount
                var amountInt = wallet.StringToAmount(model.Amount.ToString());
                var availableInt = wallet.StringToAmount(balance.Available);
                if (amountInt > availableInt)
                {
                    this.FlashError("Amount must be less then or equal to available balance");
                    return View(model);
                }
                if (amountInt <= 0)
                {
                    this.FlashError("Amount must be greather then or equal to 0");
                    return View(model);
                }

                // validate address
                if (!wallet.ValidateAddress(model.WithdrawalAddress))
                {
                    this.FlashError("Withdrawal address is not valid");
                    return View(model);
                }

                // validate kyc level
                var res = ValidateWithdrawlLimit(user, model.Asset, model.Amount);
                var withdrawalAssetAmount = res.Item2;
                if (!res.Item1)
                {
                    this.FlashError(res.Item3);
                    return View(model);
                }

                var consolidatedFundsTag = _walletProvider.ConsolidatedFundsTag();

                using (var transaction = wallet.BeginTransaction())
                {
                    // ensure tag exists
                    if (!wallet.HasTag(consolidatedFundsTag))
                    {
                        wallet.NewTag(consolidatedFundsTag);
                        wallet.Save();
                    }

                    // register withdrawal with wallet
                    var spend = wallet.RegisterPendingSpend(consolidatedFundsTag, consolidatedFundsTag,
                        model.WithdrawalAddress, amountInt, user.Id);
                    wallet.Save();
                    var businessId = spend.Meta.Id;

                    // register withdrawal with the exchange backend
                    var negativeAmount = -model.Amount;
                    try
                    {
                        via.BalanceUpdateQuery(user.Exchange.Id, model.Asset, "withdraw", businessId, negativeAmount.ToString(), null);
                    }
                    catch (System.Exception ex)
                    {
                        _logger.LogError(ex, "Failed to update (withdraw) user balance (xch id: {0}, asset: {1}, businessId: {2}, amount {3}",
                            user.Exchange.Id, model.Asset, businessId, negativeAmount);
                        throw;
                    }

                    transaction.Commit();
                }

                // register withdrawal with kyc limits
                user.AddWithdrawal(_context, model.Asset, model.Amount, withdrawalAssetAmount);
                _context.SaveChanges();

                this.FlashSuccess(string.Format("Created withdrawal: {0} {1} to {2}", model.Amount, model.Asset, model.WithdrawalAddress));
                // send email: withdrawal created
                await _emailSender.SendEmailChainWithdrawalCreatedAsync(user.Email, model.Asset, model.Amount.ToString());

                return View(model);
            }

            // If we got this far, something failed, redisplay form
            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> WithdrawalHistory(string asset)
        {
            var user = await GetUser(required: true);

            var wallet = _walletProvider.GetChain(asset);

            var spends = wallet.PendingSpendsGet(_walletProvider.ConsolidatedFundsTag(), new PendingSpendState[] { PendingSpendState.Pending, PendingSpendState.Error } )
                .Where(s => s.Meta.TagOnBehalfOf == user.Id);
            var outgoingTxs = wallet.GetTransactions(_walletProvider.ConsolidatedFundsTag())
                .Where(t => t.Meta.TagOnBehalfOf == user.Id);

            var model = new WithdrawalHistoryViewModel
            {
                User = user,
                Wallet = wallet,
                Asset = asset,
                AssetSettings = _settings.Assets[asset],
                PendingWithdrawals = spends,
                OutgoingTransactions = outgoingTxs
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> WithdrawFiat(string asset)
        {
            var user = await GetUser(required: true);

            //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
            var via = new ViaJsonRpc(_settings.AccessHttpUrl);
            var balance = via.BalanceQuery(user.Exchange.Id, asset);

            var model = new WithdrawFiatViewModel
            {
                User = user,
                Asset = asset,
                BalanceAvailable = balance.Available,
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> WithdrawFiat(WithdrawFiatViewModel model)
        {
            var user = await GetUser(required: true);

            var wallet = _walletProvider.GetFiat(model.Asset);
            var amountInt = wallet.StringToAmount(model.Amount.ToString());
            if (amountInt <= 0)
            {
                this.FlashError("Amount must be greater then 0");
                return View(model);
            }
            var decimals = _settings.Assets[model.Asset].Decimals;
            if (Utils.GetDecimalPlaces(model.Amount) > decimals)
            {
                this.FlashError($"Amount must have a maximum of {decimals} digits after the decimal place");
                return View(model);
            }

            //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
            var via = new ViaJsonRpc(_settings.AccessHttpUrl);
            var balance = via.BalanceQuery(user.Exchange.Id, model.Asset);
            // validate amount
            var availableInt = wallet.StringToAmount(balance.Available);
            if (amountInt > availableInt)
            {
                this.FlashError("Amount must be less then or equal to available balance");
                return View(model);
            }

            // validate kyc level
            var res = ValidateWithdrawlLimit(user, model.Asset, model.Amount);
            var withdrawalAssetAmount = res.Item2;
            if (!res.Item1)
            {
                this.FlashError(res.Item3);
                return View(model);
            }

            // create pending withdrawal
            var account = new BankAccount{ AccountNumber = model.WithdrawalAccount };
            var tx = wallet.RegisterPendingWithdrawal(user.Id, amountInt, account);
            model.PendingTx = tx;

            // register new withdrawal with the exchange backend
            var source = new Dictionary<string, object>();
            var amountStr = (-model.Amount).ToString();
            var depositCodeInt = long.Parse(tx.DepositCode);
            via.BalanceUpdateQuery(user.Exchange.Id, model.Asset, "withdraw", depositCodeInt, amountStr, source);
            Console.WriteLine($"Updated exchange backend");

            // save wallet (after we have posted the withdrawal to the backend)
            wallet.Save();

            // register withdrawal with kyc limits
            user.AddWithdrawal(_context, model.Asset, model.Amount, withdrawalAssetAmount);
            _context.SaveChanges();

            // send email: withdrawal created
            await _emailSender.SendEmailFiatWithdrawalCreatedAsync(user.Email, model.Asset, model.Amount.ToString(), tx.DepositCode);

            return View("WithdrawalFiatCreated", model);
        }
    }
}
