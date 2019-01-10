﻿using System;
using FundProcess.Pms.Imports.StreamTypes;
using Paillave.Etl.Core.Streams;
using Paillave.Etl.Extensions;
using Paillave.Etl.TextFile.Extensions;
using Paillave.Etl.EntityFrameworkCore.Extensions;
using FundProcess.Pms.Imports.FileDefinitions;
using Paillave.Etl.EntityFrameworkCore.StreamNodes;
using FundProcess.Pms.DataAccess.Enums;
using FundProcess.Pms.DataAccess;
using FundProcess.Pms.DataAccess.Schemas.Pms.Tables;
using Microsoft.EntityFrameworkCore;
using System.Data.SqlClient;
using Paillave.Etl.StreamNodes;

namespace FundProcess.Pms.Imports.Jobs
{
    public class ImportFiles
    {
        // private class RbcNavFileContext
        // {
        //     public RbcNavFile FileInput { get; set; }
        //     public DatabaseContext DbContext { get; set; }
        // }
        // private static IStream<RbcNavFileContext> ProcessSubfund(IStream<RbcNavFileContext> inputStream)
        // {
        //     return inputStream;
        // }
        public static void DefineRbcImportProcessWithNoCtx(ISingleStream<ImportFilesConfigNoCtx> configStream)
        {
            DefineRbcImportProcess(configStream.Select("create config with Ctx", c =>
            {
                var csb = new SqlConnectionStringBuilder();
                csb.IntegratedSecurity = true;
                csb.DataSource = c.Server;
                csb.InitialCatalog = c.Database;
                csb.MultipleActiveResultSets = true;
                return new ImportFilesConfig
                {
                    InputFilesRootFolderPath = c.InputFilesRootFolderPath,
                    DbCtx = new DatabaseContext(
                        new DbContextOptionsBuilder<DataAccess.DatabaseContext>().UseSqlServer(csb.ConnectionString).Options,
                        new TenantContext(c.EntityId, c.EntityGroupId))
                };
            }));
        }
        public static void DefineRbcImportProcess(ISingleStream<ImportFilesConfig> configStream)
        {
            var navFileStream = configStream
                .CrossApplyFolderFiles("get all Nav files", i => i.InputFilesRootFolderPath, i => i.NavFileFileNamePattern, true)
                .CrossApplyTextFile("parse nav file", new RbcNavFileDefinition());

            var dbCnxStream = configStream
                .Select("get dbcnx", i => i.DbCtx, true);

            var subFundStream = navFileStream
                .Distinct("distinct sub funds", i => i.FundCode)
                .Select("create sub funds", i => new SubFund
                {
                    InternalCode = i.FundCode,
                    Name = i.FundName,
                    CurrencyIso = i.FundCurrency,
                })
                .ThroughEntityFrameworkCore("save sub funds", dbCnxStream, (SubFund sf1, SubFund sf2) => sf1.InternalCode == sf2.InternalCode);

            var shareClassStream = navFileStream
                .Distinct("distinct share classes", i => $"{i.FundCode}_{i.IsinCode}")
                .Lookup("link to related sub fund", subFundStream, i => i.FundCode, i => i.InternalCode, (l, r) => new { FromFile = l, FromDb = r })
                .Select("create share classes", i => new ShareClass
                {
                    InternalCode = $"{i.FromFile.FundCode}_{i.FromFile.IsinCode}",
                    Name = $"{i.FromFile.FundName}_{i.FromFile.IsinCode}",
                    CurrencyIso = i.FromFile.Currency,
                    SubFundId = i.FromDb.Id,
                    Isin = i.FromFile.IsinCode
                })
                .ThroughEntityFrameworkCore("save share classes", dbCnxStream, (ShareClass sc1, ShareClass sc2) => sc1.InternalCode == sc2.InternalCode);

            navFileStream
                .Distinct("distinct sub funds and date", i => new { i.FundCode, i.NavDate })
                .Unpivot("unpivot sub funds historical values", FieldsToUnpivot.Create(
                         (RbcNavFile i) => new { Type = HistoricalValueType.TNA, Value = (decimal?)i.FundTotalNetAsset },
                         (RbcNavFile i) => new { Type = HistoricalValueType.RED, Value = i.AmountRedemption },
                         (RbcNavFile i) => new { Type = HistoricalValueType.SUB, Value = i.AmountSubscription }
                    ),
                    (i, j) => new
                    {
                        Date = i.NavDate,
                        j.Type,
                        FundInternalCode = i.FundCode,
                        j.Value,
                    })
            .Where("Exclude empty values from sub fund hv", i => i.Value != null)
            .Lookup("get hv related sub fund", subFundStream, i => i.FundInternalCode, i => i.InternalCode, (l, r) => new { FromFile = l, FromDb = r })
            .Select("create sub fund hv", dbCnxStream, (i, dbCnx) => dbCnx.UpdateForMultiTenancy(new HistoricalValue
            {
                SecurityId = i.FromDb.Id,
                Date = i.FromFile.Date,
                Type = i.FromFile.Type,
                Value = i.FromFile.Value.Value
            }))
            .ThroughEntityFrameworkCore("save sub fund hv", dbCnxStream);


            navFileStream
                .Unpivot("unpivot share class historical values", FieldsToUnpivot.Create(
                         (RbcNavFile i) => new { Type = HistoricalValueType.TNA, Value = i.TotalNetAsset },
                         (RbcNavFile i) => new { Type = HistoricalValueType.MKT, Value = i.NavPerShare }
                    ),
                    (i, j) => new
                    {
                        Date = i.NavDate,
                        j.Type,
                        ShareClassInternalCode = $"{i.FundCode}_{i.IsinCode}",
                        j.Value,
                    })
                .Lookup("get hv related share class", shareClassStream, i => i.ShareClassInternalCode, i => i.InternalCode, (l, r) => new { FromFile = l, FromDb = r })
                .Select("create share class hv", dbCnxStream, (i, dbCnx) => dbCnx.UpdateForMultiTenancy(new HistoricalValue
                {
                    SecurityId = i.FromDb.Id,
                    Date = i.FromFile.Date,
                    Type = i.FromFile.Type,
                    Value = i.FromFile.Value
                }))
                .ThroughEntityFrameworkCore("save share class hv", dbCnxStream);

            var posFileStream = configStream
                .CrossApplyFolderFiles("get all position files", i => i.InputFilesRootFolderPath, i => i.PositionFileFileNamePattern, true)
                .CrossApplyTextFile("parse position file", new RbcPositionFileDefinition(), i => i.Name);

            var compositionStream = posFileStream
                .Distinct("distinct composition for a date", i => new { i.FundCode, i.NavDate })
                .Lookup("get composition sub fund", subFundStream, i => i.FundCode, i => i.InternalCode, (l, r) => new { FromFile = l, SubFund = r })
                .ThroughEntityFrameworkCore("save composition", dbCnxStream, i => new PortfolioComposition
                {
                    Date = i.FromFile.NavDate,
                    PortfolioId = i.SubFund.Id
                },
                (i, j) => i.PortfolioId == j.PortfolioId && i.Date == j.Date,
                (i, j) => new
                {
                    i.FromFile,
                    Composition = j
                });

            var composingSecurityStream = posFileStream
                .Distinct("distinct positions", i => new { i.FundCode, i.InternalNumber, i.NavDate })
                .Select("create security for composition", i => new
                {
                    FromFile = i,
                    Security = CreateSecurityFromInstrumentType(i.InvestmentType, i.Currency, i.IsinCode, i.InstrumentName, i.InternalNumber, i.NextCouponDate),
                })
                .Where("exclude entries that don't match with a security", i => i.Security != null)
                .ThroughEntityFrameworkCore("save security for composition", dbCnxStream,
                    i => i.Security,
                    (l, r) => l.InternalCode == r.InternalCode,
                    (i, j) => i)
                .Lookup("get related composition", compositionStream,
                    i => new { i.FromFile.InternalNumber, i.FromFile.FundCode, i.FromFile.NavDate },
                    i => new { i.FromFile.InternalNumber, i.FromFile.FundCode, i.FromFile.NavDate },
                    (l, r) => new
                    {
                        l.FromFile.Quantity,
                        l.FromFile.MarketValueInFdCcy,
                        l.FromFile.MarketValueInSecCcy,
                        l.FromFile.PercNav,
                        r.Composition,
                        l.Security
                    })
                .Select("create position", i => new Position
                {
                    SecurityId = i.Security.Id,
                    MarketValueInPortfolioCcy = i.MarketValueInFdCcy,
                    MarketValueInSecurityCcy = i.MarketValueInSecCcy,
                    PortfolioCompositionId = i.Composition.Id,
                    Value = i.Quantity,
                    Weight = i.PercNav
                })
                .ThroughEntityFrameworkCore("save position", dbCnxStream, (i, j) => i.SecurityId == j.SecurityId && i.PortfolioCompositionId == j.PortfolioCompositionId);
        }
        private static Security CreateSecurityFromInstrumentType(string rbcType, string currencyIso, string isin, string name, string internalCode, DateTime? nextCouponDate)
        {
            var rbcCode = rbcType.Split(':')[0].Trim();

            switch (rbcCode)
            {
                case "100":
                case "102":
                case "103":
                case "120":
                case "117": // REITS
                case "118": // NON G.T. REITS
                case "411": // SICAF
                    return new Equity { InternalCode = internalCode, CurrencyIso = currencyIso, Isin = isin, Name = name };
                case "484":
                case "485":
                    return new SubFund { InternalCode = internalCode, CurrencyIso = currencyIso, Isin = isin, Name = name };
                case "200":
                case "201":
                case "270": // Commercial paper
                case "271": // Certificate of Deposit
                case "202": // "202 : ZERO COUPON BONDS"
                    return new Bond { InternalCode = internalCode, CurrencyIso = currencyIso, Isin = isin, Name = name, NextCouponDate = nextCouponDate };
                case "603": // Call/Put
                    return new Derivative { InternalCode = internalCode, CurrencyIso = currencyIso, Isin = isin, Name = name };
            }
            return null;
            //throw new Exception("Unrecognized code: " + rbcType);
            //100 : SHARES
            //484 : Investment Funds - UCITS- French
            //117 : G.T. REITS
            //485 : Investment Funds - UCITS- European
            //200 : STRAIGHT BONDS
            //201 : FLOATING RATE BONDS
            //270 : COMMERCIAL PAPER
            //202 : ZERO COUPON BONDS
        }

    }
}
