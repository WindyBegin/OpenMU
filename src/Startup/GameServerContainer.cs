﻿// <copyright file="GameServerContainer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Startup;

using System.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameServer;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;
using MUnique.OpenMU.Web.AdminPanel.Services;

/// <summary>
/// A container which keeps all <see cref="IGameServer"/>s in one <see cref="IHostedService"/>.
/// </summary>
public sealed class GameServerContainer : ServerContainerBase, IDisposable
{
    private readonly ILogger<GameServerContainer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IList<IManageableServer> _servers;
    private readonly IPersistenceContextProvider _persistenceContextProvider;
    private readonly ConnectServerContainer _connectServerContainer;
    private readonly IGuildServer _guildServer;
    private readonly ILoginServer _loginServer;
    private readonly IFriendServer _friendServer;
    private readonly IIpAddressResolver _ipResolver;
    private readonly PlugInManager _plugInManager;
    private readonly IDictionary<int, IGameServer> _gameServers;
    private readonly IEventPublisher _eventPublisher;

    /// <summary>
    /// Initializes a new instance of the <see cref="GameServerContainer" /> class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="servers">The servers.</param>
    /// <param name="gameServers">The game servers.</param>
    /// <param name="persistenceContextProvider">The persistence context provider.</param>
    /// <param name="connectServerContainer">The connect server container.</param>
    /// <param name="guildServer">The guild server.</param>
    /// <param name="loginServer">The login server.</param>
    /// <param name="friendServer">The friend server.</param>
    /// <param name="ipResolver">The ip resolver.</param>
    /// <param name="plugInManager">The plug in manager.</param>
    /// <param name="setupService">The setup service.</param>
    public GameServerContainer(
        ILoggerFactory loggerFactory,
        IList<IManageableServer> servers,
        IDictionary<int, IGameServer> gameServers,
        IPersistenceContextProvider persistenceContextProvider,
        ConnectServerContainer connectServerContainer,
        IGuildServer guildServer,
        ILoginServer loginServer,
        IFriendServer friendServer,
        IIpAddressResolver ipResolver,
        PlugInManager plugInManager,
        SetupService setupService)
        : base(setupService, loggerFactory.CreateLogger<GameServerContainer>())
    {
        this._loggerFactory = loggerFactory;
        this._servers = servers;
        this._gameServers = gameServers;
        this._persistenceContextProvider = persistenceContextProvider;
        this._connectServerContainer = connectServerContainer;
        this._guildServer = guildServer;
        this._loginServer = loginServer;
        this._friendServer = friendServer;
        this._ipResolver = ipResolver;
        this._plugInManager = plugInManager;

        this._logger = this._loggerFactory.CreateLogger<GameServerContainer>();
        this._eventPublisher = new InMemoryEventPublisher(this._gameServers, this._friendServer, this._guildServer);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var gameServer in this._gameServers.Values)
        {
            (gameServer as IDisposable)?.Dispose();
        }
    }

    /// <inheritdoc />
#pragma warning disable CS1998
    protected override async Task StartInnerAsync(CancellationToken cancellationToken)
#pragma warning restore CS1998
    {
        using var persistenceContext = this._persistenceContextProvider.CreateNewConfigurationContext();
        await this.LoadGameClientDefinitionsAsync(persistenceContext);
        foreach (var gameServerDefinition in await persistenceContext.GetAsync<GameServerDefinition>())
        {
            using var loggerScope = this._logger.BeginScope("GameServer: {0}", gameServerDefinition.ServerID);
            var gameServer = new GameServer(gameServerDefinition, this._guildServer, this._eventPublisher, this._loginServer, this._persistenceContextProvider, this._friendServer, this._loggerFactory, this._plugInManager);
            foreach (var endpoint in gameServerDefinition.Endpoints)
            {
                gameServer.AddListener(new DefaultTcpGameServerListener(endpoint, gameServer.CreateServerInfo(), gameServer.Context, this._connectServerContainer.GetObserver(endpoint.Client!), this._ipResolver, this._loggerFactory));
            }

            this._servers.Add(gameServer);
            this._gameServers.Add(gameServer.Id, gameServer);
            this._logger.LogInformation($"Game Server {gameServer.Id} - [{gameServer.Description}] initialized");
        }
    }

    /// <inheritdoc />
    protected override async Task StopInnerAsync(CancellationToken cancellationToken)
    {
        foreach (var gameServer in this._gameServers.Values)
        {
            await gameServer.StopAsync(cancellationToken);
            this._servers.Remove(gameServer);
        }

        this._gameServers.Clear();
    }

    private async ValueTask LoadGameClientDefinitionsAsync(IContext persistenceContext)
    {
        var versions = (await persistenceContext.GetAsync<GameClientDefinition>()).ToList();
        foreach (var gameClientDefinition in versions)
        {
            ClientVersionResolver.Register(
                gameClientDefinition.Version,
                new ClientVersion(gameClientDefinition.Season, gameClientDefinition.Episode, gameClientDefinition.Language));
        }

        if (versions.FirstOrDefault() is { } firstVersion)
        {
            ClientVersionResolver.DefaultVersion = ClientVersionResolver.Resolve(firstVersion.Version);
        }
    }
}