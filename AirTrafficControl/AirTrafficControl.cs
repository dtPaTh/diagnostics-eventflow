﻿using AirTrafficControl.Common;
using AirTrafficControl.Interfaces;
using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Client;
using Microsoft.ServiceFabric.Actors.Runtime;
using Microsoft.ServiceFabric.Services.Communication.Client;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Validation;

namespace AirTrafficControl
{
    [StatePersistence(StatePersistence.Persisted)]
    public class AirTrafficControl : Actor, IAirTrafficControl, IRemindable
    {
        private const string TimePassedReminder = "AirTrafficControl.TimePassedReminder";
        private const string FrontendServiceName = "fabric:/AirTrafficControlApplication/AirTrafficControlWeb";
        private const string FlyingAirplaneIDsStateProperty = "FlyingAirplaneIDs";
        private const string CurrentTimeStateProperty = "CurrentTime";

        private delegate Task AirplaneController(IAirplane airplaneProxy, AirplaneActorState airplaneActorState, IDictionary<string, AirplaneState> projectedAirplaneStates);

        private readonly IDictionary<Type, AirplaneController> AirplaneControllers;
        private ServicePartitionClient<HttpCommunicationClient> frontendCommunicationClient;
    
        public AirTrafficControl(): base()
        {
            AirplaneControllers = new Dictionary<Type, AirplaneController>()
            {
                { typeof(TaxiingState), HandleAirplaneTaxiing },
                { typeof(DepartingState), HandleAirplaneDeparting },
                { typeof(HoldingState), HandleAirplaneHolding },
                { typeof(EnrouteState), HandleAirplaneEnroute },
                { typeof(ApproachState), HandleAirplaneApproaching },
                { typeof(LandedState), HandleAirplaneLanded }
            };
        }        

        protected override async Task OnActivateAsync()
        {
            await base.OnActivateAsync();
            await this.StateManager.TryAddStateAsync<List<string>>(FlyingAirplaneIDsStateProperty, new List<string>());
            await this.StateManager.TryAddStateAsync<int>(CurrentTimeStateProperty, 0);

            this.frontendCommunicationClient = new ServicePartitionClient<HttpCommunicationClient>(new HttpCommunicationClientFactory(), new Uri(FrontendServiceName));
        }
        public async Task<IEnumerable<string>> GetFlyingAirplaneIDs()
        {
            var flyingAirplaneIDs = await GetFlyingAirplaneIDsInternal();
            return flyingAirplaneIDs.AsEnumerable();
        }

        public async Task StartNewFlight(FlightPlan flightPlan)
        {
            Requires.NotNull(flightPlan, "flightPlan");
            flightPlan.Validate();

            var flyingAirplaneIDs = await GetFlyingAirplaneIDsInternal();

            if (flyingAirplaneIDs.Contains(flightPlan.AirplaneID))
            {
                // In real life airplanes can have multiple flight plans filed, just for different times. But here we assume there can be only one flight plan per airplane
                throw new InvalidOperationException("The airplane " + flightPlan.AirplaneID + " is already flying");
            }

            // Make sure we have a reminder set up so that we can simulate the flight
            IActorReminder reminder = null;
            try
            {
                reminder = this.GetReminder(TimePassedReminder);
            }
            catch { }
            if (reminder == null)
            {
                int currentTime = await this.StateManager.GetStateAsync<int>(CurrentTimeStateProperty);
                ActorEventSource.Current.ActorMessage(this, "ATC: Starting the world timer, current time is {0}", currentTime);                
                await this.RegisterReminderAsync(TimePassedReminder, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10));
            }

            ActorId actorID = new ActorId(flightPlan.AirplaneID);
            IAirplane airplane = ActorProxy.Create<IAirplane>(actorID);
            await airplane.StartFlightAsync(flightPlan);
            flyingAirplaneIDs.Add(flightPlan.AirplaneID);

            await SetFlyingAirplaneIDs(flyingAirplaneIDs);

            ActorEventSource.Current.ActorMessage(this, "ATC: new filght plan received for {0}: departing from {1}, destination {2}.",
                flightPlan.AirplaneID,
                flightPlan.DeparturePoint.DisplayName,
                flightPlan.Destination.DisplayName);
        }

        public async Task ReceiveReminderAsync(string reminderName, byte[] context, TimeSpan dueTime, TimeSpan period)
        {
            if (!TimePassedReminder.Equals(reminderName, StringComparison.Ordinal))
            {
                return;
            }

            var flyingAirplaneIDs = await GetFlyingAirplaneIDsInternal();
            int currentTime = await this.StateManager.GetStateAsync<int>(CurrentTimeStateProperty);
            currentTime++;

            if (flyingAirplaneIDs.Count == 0)
            {
                ActorEventSource.Current.ActorMessage(this, "ATC: Time is {0} No airplanes flying, shutting down the world timer", currentTime);
                var reminder = this.GetReminder(TimePassedReminder);
                await this.UnregisterReminderAsync(reminder);
                return;
            }

            var airplaneProxies = CreateAirplaneProxies(flyingAirplaneIDs);
            var airplaneActorStatesByDepartureTime = (await Task.WhenAll(flyingAirplaneIDs.Select(id => airplaneProxies[id].GetStateAsync())))
                                                       .Where(state => !(state.AirplaneState is UnknownLocationState))
                                                       .OrderBy(state => (state.AirplaneState is TaxiingState) ? int.MaxValue : state.DepartureTime);
            var newAirplaneStates = new Dictionary<string, AirplaneState>();

            foreach(var airplaneActorState in airplaneActorStatesByDepartureTime)
            {
                var controllerFunction = this.AirplaneControllers[airplaneActorState.AirplaneState.GetType()];
                Assumes.NotNull(controllerFunction);

                await controllerFunction(airplaneProxies[airplaneActorState.FlightPlan.AirplaneID], airplaneActorState, newAirplaneStates);
            }            

            await Task.WhenAll(newAirplaneStates.Keys.Select(airplaneID => airplaneProxies[airplaneID].TimePassedAsync(currentTime)));

            // Notify anybody who is listening about new airplane states
            var airplaneStateNotifications = airplaneActorStatesByDepartureTime.Select(airplaneActorState =>
                                                new AirplaneStateDto(newAirplaneStates[airplaneActorState.FlightPlan.AirplaneID], airplaneActorState.FlightPlan));
            NotifyFlightStatus(airplaneStateNotifications);
        }

        private Dictionary<string, IAirplane> CreateAirplaneProxies(List<string> flyingAirplaneIDs)
        {
            var retval = new Dictionary<string, IAirplane>();
            foreach (var airplaneID in flyingAirplaneIDs)
            {
                retval.Add(airplaneID, ActorProxy.Create<IAirplane>(new ActorId(airplaneID)));
            }
            return retval;
        }

        private void NotifyFlightStatus(IEnumerable<AirplaneStateDto> airplaneStateNotifications)
        {
            try
            {
                this.frontendCommunicationClient.InvokeWithRetryAsync(async communicationClient =>
                        {
                            try
                            {
                                var content = new StringContent(JsonConvert.SerializeObject(airplaneStateNotifications), System.Text.Encoding.UTF8, "application/json");
                                await communicationClient.HttpClient.PostAsync("/api/notify/flight-status", content);

                                ActorEventSource.Current.ActorMessage(this, "Flight status notification sent");
                            }
                            catch (Exception e)
                            {
                                ActorEventSource.Current.FlightStatusNotificationFailed(e.ToString());
                                throw;
                            }
                        });
            }
            catch (Exception e)
            {
                ActorEventSource.Current.FlightStatusNotificationFailed(e.ToString());
            }
        }

        private async Task HandleAirplaneLanded(IAirplane airplaneProxy, AirplaneActorState airplaneActorState, IDictionary<string, AirplaneState> projectedAirplaneStates)
        {
            // Just remove the airplane form the flying airplanes set
            var flyingAirplaneIDs = await GetFlyingAirplaneIDsInternal();
            flyingAirplaneIDs.Remove(airplaneActorState.FlightPlan.AirplaneID);
            await SetFlyingAirplaneIDs(flyingAirplaneIDs);
            ActorEventSource.Current.ActorMessage(this, "ATC: Airplane {0} has landed and is no longer tracked", airplaneActorState.FlightPlan.AirplaneID);
        }

        private Task HandleAirplaneApproaching(IAirplane airplaneProxy, AirplaneActorState airplaneActorState, IDictionary<string, AirplaneState> projectedAirplaneStates)
        {
            // We assume that every approach is successful, so just make a note that the airplane will be in the Landed state
            FlightPlan flightPlan = airplaneActorState.FlightPlan;
            Assumes.NotNull(flightPlan);
            projectedAirplaneStates[flightPlan.AirplaneID] = new LandedState(flightPlan.Destination);
            return Task.FromResult(true);
        }

        private async Task HandleAirplaneEnroute(IAirplane airplaneProxy, AirplaneActorState airplaneActorState, IDictionary<string, AirplaneState> projectedAirplaneStates)
        {
            EnrouteState enrouteState = (EnrouteState)airplaneActorState.AirplaneState;
            FlightPlan flightPlan = airplaneActorState.FlightPlan;

            if (enrouteState.To == flightPlan.Destination)
            {
                // Any other airplanes cleared for landing at this airport?
                if (projectedAirplaneStates.Values.OfType<ApproachState>().Any(state => state.Airport == flightPlan.Destination))
                {
                    projectedAirplaneStates[flightPlan.AirplaneID] = new HoldingState(flightPlan.Destination);
                    await airplaneProxy.ReceiveInstructionAsync(new HoldInstruction(flightPlan.Destination)).ConfigureAwait(false);
                    ActorEventSource.Current.ActorMessage(this, "ATC: Issued holding instruction for {0} at {1} because another airplane has been cleared for approach at the same airport", 
                        flightPlan.AirplaneID, flightPlan.Destination.DisplayName);
                }
                else
                {
                    projectedAirplaneStates[flightPlan.AirplaneID] = new ApproachState(flightPlan.Destination);
                    await airplaneProxy.ReceiveInstructionAsync(new ApproachClearance(flightPlan.Destination)).ConfigureAwait(false);
                    ActorEventSource.Current.ActorMessage(this, "ATC: Issued approach clearance for {0} at {1}", flightPlan.AirplaneID, flightPlan.Destination.DisplayName);
                }
            }
            else
            {
                Fix nextFix = enrouteState.Route.GetNextFix(enrouteState.To, flightPlan.Destination);

                // Is another airplane destined to the same fix?
                if (projectedAirplaneStates.Values.OfType<EnrouteState>().Any(state => state.To == nextFix))
                {
                    // Hold at the end of the current route leg
                    projectedAirplaneStates[flightPlan.AirplaneID] = new HoldingState(enrouteState.To);
                    await airplaneProxy.ReceiveInstructionAsync(new HoldInstruction(enrouteState.To)).ConfigureAwait(false);
                    ActorEventSource.Current.ActorMessage(this, "ATC: Issued holding instruction for {0} at {1} because of traffic contention at {2}",
                        flightPlan.AirplaneID, enrouteState.To.DisplayName, nextFix.DisplayName);
                }
                else
                {
                    // Just let it proceed to next fix, no instruction necessary
                    projectedAirplaneStates[flightPlan.AirplaneID] = new EnrouteState(enrouteState.To, nextFix, enrouteState.Route);
                    ActorEventSource.Current.ActorMessage(this, "ATC: Airplane {0} is flying from {1} to {2}, next fix {3}",
                        flightPlan.AirplaneID, enrouteState.From.DisplayName, enrouteState.To.DisplayName, nextFix.DisplayName);
                }
            }
        }

        private async Task HandleAirplaneHolding(IAirplane airplaneProxy, AirplaneActorState airplaneActorState, IDictionary<string, AirplaneState> projectedAirplaneStates)
        {
            HoldingState holdingState = (HoldingState)airplaneActorState.AirplaneState;
            FlightPlan flightPlan = airplaneActorState.FlightPlan;            

            // Case 1: airplane holding at destination airport
            if (holdingState.Fix == flightPlan.Destination)
            {
                // Grant approach clearance if no other airplane is cleared for approach at the same airport.
                if (!projectedAirplaneStates.Values.OfType<ApproachState>().Any(state => state.Airport == flightPlan.Destination))
                {
                    projectedAirplaneStates[flightPlan.AirplaneID] = new ApproachState(flightPlan.Destination);
                    await airplaneProxy.ReceiveInstructionAsync(new ApproachClearance(flightPlan.Destination)).ConfigureAwait(false);
                    ActorEventSource.Current.ActorMessage(this, "ATC: Airplane {0} has been cleared for approach at {1}", flightPlan.AirplaneID, flightPlan.Destination.DisplayName);
                }
                else
                {
                    projectedAirplaneStates[flightPlan.AirplaneID] = new HoldingState(flightPlan.Destination);
                    ActorEventSource.Current.ActorMessage(this, "ATC: Airplane {0} should continue holding at {1} because of other traffic landing", 
                        flightPlan.AirplaneID, flightPlan.Destination.DisplayName);
                }

                return;
            }

            // Case 2: holding at some point enroute
            Route route = Universe.Current.GetRouteBetween(flightPlan.DeparturePoint, flightPlan.Destination);
            Assumes.NotNull(route);
            Fix nextFix = route.GetNextFix(holdingState.Fix, flightPlan.Destination);
            Assumes.NotNull(nextFix);
            
            if (projectedAirplaneStates.Values.OfType<EnrouteState>().Any(enrouteState => enrouteState.To == nextFix))
            {
                projectedAirplaneStates[flightPlan.AirplaneID] = holdingState;
                ActorEventSource.Current.ActorMessage(this, "ATC: Airplane {0} should continue holding at {1} because of traffic contention at {2}. Assuming compliance with previous instruction, no new instructions issued.",
                    flightPlan.AirplaneID, holdingState.Fix.DisplayName, nextFix.DisplayName);
            }
            else
            {
                projectedAirplaneStates[flightPlan.AirplaneID] = new EnrouteState(holdingState.Fix, nextFix, route);
                // We always optmimistically give an enroute clearance all the way to the destination
                await airplaneProxy.ReceiveInstructionAsync(new EnrouteClearance(flightPlan.Destination)).ConfigureAwait(false);
                ActorEventSource.Current.ActorMessage(this, "ATC: Airplane {0} should end holding at {1} and proceed to destination, next fix {2}. Issued new enroute clearance.",
                    flightPlan.AirplaneID, holdingState.Fix.DisplayName, nextFix.DisplayName);
            }
        }

        private async Task HandleAirplaneDeparting(IAirplane airplaneProxy, AirplaneActorState airplaneActorState, IDictionary<string, AirplaneState> projectedAirplaneStates)
        {
            DepartingState departingState = (DepartingState)airplaneActorState.AirplaneState;
            FlightPlan flightPlan = airplaneActorState.FlightPlan;

            Route route = Universe.Current.GetRouteBetween(flightPlan.DeparturePoint, flightPlan.Destination);
            Assumes.NotNull(route);
            Fix nextFix = route.GetNextFix(departingState.Airport, flightPlan.Destination);
            Assumes.NotNull(nextFix);

            if (projectedAirplaneStates.Values.OfType<EnrouteState>().Any(enrouteState => enrouteState.To == nextFix))
            {
                projectedAirplaneStates[flightPlan.AirplaneID] = new HoldingState(departingState.Airport);
                await airplaneProxy.ReceiveInstructionAsync(new HoldInstruction(departingState.Airport)).ConfigureAwait(false);
                ActorEventSource.Current.ActorMessage(this, "ATC: Issued holding instruction for {0} at {1} because of traffic contention at {2}",
                    flightPlan.AirplaneID, departingState.Airport.DisplayName, nextFix.DisplayName);
            }
            else
            {
                projectedAirplaneStates[flightPlan.AirplaneID] = new EnrouteState(departingState.Airport, nextFix, route);
                ActorEventSource.Current.ActorMessage(this, "ATC: Airplane {0} completed departure from {1} and proceeds enroute to destination, next fix {2}",
                    flightPlan.AirplaneID, departingState.Airport.DisplayName, nextFix.DisplayName);
            }
        }

        private async Task HandleAirplaneTaxiing(IAirplane airplaneProxy, AirplaneActorState airplaneActorState, IDictionary<string, AirplaneState> projectedAirplaneStates)
        {
            TaxiingState taxiingState = (TaxiingState)airplaneActorState.AirplaneState;
            FlightPlan flightPlan = airplaneActorState.FlightPlan;

            if (projectedAirplaneStates.Values.OfType<DepartingState>().Any(state => state.Airport == flightPlan.DeparturePoint))
            {
                projectedAirplaneStates[flightPlan.AirplaneID] = taxiingState;
                ActorEventSource.Current.ActorMessage(this, "ATC: Airplane {0} continue taxi at {1}, another airplane departing", 
                    flightPlan.AirplaneID, flightPlan.DeparturePoint.DisplayName);
            }
            else
            {
                projectedAirplaneStates[flightPlan.AirplaneID] = new DepartingState(flightPlan.DeparturePoint);
                await airplaneProxy.ReceiveInstructionAsync(new TakeoffClearance(flightPlan.DeparturePoint)).ConfigureAwait(false);
                ActorEventSource.Current.ActorMessage(this, "ATC: Airplane {0} received takeoff clearance at {1}",
                    flightPlan.AirplaneID, flightPlan.DeparturePoint);
            }
        }

        private Task<List<string>> GetFlyingAirplaneIDsInternal()
        {
            return this.StateManager.GetStateAsync<List<string>>(FlyingAirplaneIDsStateProperty);
        }

        private Task SetFlyingAirplaneIDs(List<string> flyingAirplaneIDs)
        {
            return this.StateManager.SetStateAsync<List<string>>(FlyingAirplaneIDsStateProperty, flyingAirplaneIDs);
        }
    }
}
