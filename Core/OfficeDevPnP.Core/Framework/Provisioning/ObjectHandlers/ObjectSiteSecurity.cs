﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SharePoint.Client;
using OfficeDevPnP.Core.Framework.Provisioning.Model;
using User = OfficeDevPnP.Core.Framework.Provisioning.Model.User;
using OfficeDevPnP.Core.Diagnostics;
using Microsoft.SharePoint.Client.Utilities;
using OfficeDevPnP.Core.Framework.Provisioning.ObjectHandlers.TokenDefinitions;
using RoleAssignment = OfficeDevPnP.Core.Framework.Provisioning.Model.RoleAssignment;
using RoleDefinition = Microsoft.SharePoint.Client.RoleDefinition;

namespace OfficeDevPnP.Core.Framework.Provisioning.ObjectHandlers
{
    internal class ObjectSiteSecurity : ObjectHandlerBase
    {
        public override string Name
        {
            get { return "Site Security"; }
        }
        public override TokenParser ProvisionObjects(Web web, ProvisioningTemplate template, TokenParser parser, ProvisioningTemplateApplyingInformation applyingInformation)
        {
            using (var scope = new PnPMonitoredScope(this.Name))
            {
                // Changed by Paolo Pialorsi to embrace the new sub-site attributes to break role inheritance and copy role assignments
                // if this is a sub site then we're not provisioning security as by default security is inherited from the root site
                //if (web.IsSubSite() && !template.Security.BreakRoleInheritance)
                //{
                //    scope.LogDebug(CoreResources.Provisioning_ObjectHandlers_SiteSecurity_Context_web_is_subweb__skipping_site_security_provisioning);
                //    return parser;
                //}

                if (web.IsSubSite() && template.Security.BreakRoleInheritance)
                {
                    web.BreakRoleInheritance(template.Security.CopyRoleAssignments, template.Security.ClearSubscopes);
                    web.Update();
                    web.Context.ExecuteQueryRetry();
                }

                var siteSecurity = template.Security;

                var ownerGroup = web.AssociatedOwnerGroup;
                var memberGroup = web.AssociatedMemberGroup;
                var visitorGroup = web.AssociatedVisitorGroup;

                web.Context.Load(ownerGroup, o => o.Title, o => o.Users);
                web.Context.Load(memberGroup, o => o.Title, o => o.Users);
                web.Context.Load(visitorGroup, o => o.Title, o => o.Users);
                web.Context.Load(web.SiteUsers);

                web.Context.ExecuteQueryRetry();

                if (!ownerGroup.ServerObjectIsNull())
                {
                    AddUserToGroup(web, ownerGroup, siteSecurity.AdditionalOwners, scope, parser);
                }
                if (!memberGroup.ServerObjectIsNull())
                {
                    AddUserToGroup(web, memberGroup, siteSecurity.AdditionalMembers, scope, parser);
                }
                if (!visitorGroup.ServerObjectIsNull())
                {
                    AddUserToGroup(web, visitorGroup, siteSecurity.AdditionalVisitors, scope, parser);
                }

                foreach (var siteGroup in siteSecurity.SiteGroups)
                {
                    Group spGroup;
                    var allGroups = web.Context.LoadQuery(web.SiteGroups.Include(gr => gr.LoginName));
                    web.Context.ExecuteQueryRetry();

                    string parsedGroupTitle = parser.ParseString(siteGroup.Title);
                    string parsedGroupOwner = parser.ParseString(siteGroup.Owner);
                    string parsedGroupDescription = parser.ParseString(siteGroup.Description);

                    if (!web.GroupExists(parsedGroupTitle))
                    {
                        spGroup = CreateSpGroup(web, parser, scope, parsedGroupTitle, parsedGroupDescription, parsedGroupOwner, siteGroup, allGroups);
                    }
                    else
                    {
                        spGroup = UpdateSpGroup(web, parsedGroupTitle, parsedGroupDescription, siteGroup, parsedGroupOwner, allGroups, scope);
                    }
                    if (spGroup != null && siteGroup.Members.Any())
                    {
                        AddUserToGroup(web, spGroup, siteGroup.Members, scope, parser);
                    }
                }

                foreach (var admin in siteSecurity.AdditionalAdministrators)
                {
                    var parsedAdminName = parser.ParseString(admin.Name);
                    var user = web.EnsureUser(parsedAdminName);
                    user.IsSiteAdmin = true;
                    user.Update();
                    web.Context.ExecuteQueryRetry();
                }

                if (!web.IsSubSite() && siteSecurity.SiteSecurityPermissions != null)
                // Only manage permissions levels on sitecol level
                {
                    var existingRoleDefinitions =
                        web.Context.LoadQuery(web.RoleDefinitions.Include(wr => wr.Name, wr => wr.BasePermissions,
                            wr => wr.Description));
                    web.Context.ExecuteQueryRetry();

                    if (siteSecurity.SiteSecurityPermissions.RoleDefinitions.Any())
                    {
                        foreach (var templateRoleDefinition in siteSecurity.SiteSecurityPermissions.RoleDefinitions)
                        {
                            var roleDefinitions = existingRoleDefinitions as RoleDefinition[] ??
                                                  existingRoleDefinitions.ToArray();
                            var siteRoleDefinition =
                                roleDefinitions.FirstOrDefault(
                                    erd => erd.Name == parser.ParseString(templateRoleDefinition.Name));
                            if (siteRoleDefinition == null)
                            {
                                CreateRoleDefinition(web, parser, scope, templateRoleDefinition);
                            }
                            else
                            {
                                UpdateRoleDefinition(web, parser, siteRoleDefinition, templateRoleDefinition, scope);
                            }
                        }
                    }
                }

                if (siteSecurity.SiteSecurityPermissions != null)
                {
                    //Handle Roleassignments - also on subsites with broken permissions!
                    if (!web.IsSubSite() || web.IsSubSite() && template.Security.BreakRoleInheritance)
                    {
                        var webRoleDefinitions = web.Context.LoadQuery(web.RoleDefinitions);
                        var groups = web.Context.LoadQuery(web.SiteGroups.Include(g => g.LoginName));
                        web.Context.ExecuteQueryRetry();

                        if (siteSecurity.SiteSecurityPermissions.RoleAssignments.Any())
                        {
                            foreach (var roleAssignment in siteSecurity.SiteSecurityPermissions.RoleAssignments)
                            {
                                EnsureRoleAssignment(web, parser, groups, roleAssignment, webRoleDefinitions);
                            }
                        }
                    }
                }
            }
            return parser;
        }

        private static void EnsureRoleAssignment(Web web, TokenParser parser, IEnumerable<Group> groups, RoleAssignment roleAssignment,
            IEnumerable<RoleDefinition> webRoleDefinitions)
        {
            Principal principal = groups.FirstOrDefault(g => g.LoginName == parser.ParseString(roleAssignment.Principal));
            if (principal == null)
            {
                principal = web.EnsureUser(parser.ParseString(roleAssignment.Principal));
            }

            var roleDefinitionBindingCollection = new RoleDefinitionBindingCollection(web.Context);

            var roleDefinition =
                webRoleDefinitions.FirstOrDefault(r => r.Name == parser.ParseString(roleAssignment.RoleDefinition));

            if (roleDefinition != null)
            {
                roleDefinitionBindingCollection.Add(roleDefinition);
            }
            web.RoleAssignments.Add(principal, roleDefinitionBindingCollection);
            web.Context.ExecuteQueryRetry();
        }

        private static void CreateRoleDefinition(Web web, TokenParser parser, PnPMonitoredScope scope,
            Model.RoleDefinition templateRoleDefinition)
        {
            scope.LogDebug("Creation role definition {0}", parser.ParseString(templateRoleDefinition.Name));
            var roleDefinitionCI = new RoleDefinitionCreationInformation();
            roleDefinitionCI.Name = parser.ParseString(templateRoleDefinition.Name);
            roleDefinitionCI.Description = parser.ParseString(templateRoleDefinition.Description);
            BasePermissions basePermissions = new BasePermissions();

            foreach (var permission in templateRoleDefinition.Permissions)
            {
                basePermissions.Set(permission);
            }

            roleDefinitionCI.BasePermissions = basePermissions;

            web.RoleDefinitions.Add(roleDefinitionCI);
            web.Context.ExecuteQueryRetry();
        }

        private static void UpdateRoleDefinition(Web web, TokenParser parser, RoleDefinition siteRoleDefinition,
            Model.RoleDefinition templateRoleDefinition, PnPMonitoredScope scope)
        {
            var isDirty = false;
            if (siteRoleDefinition.Description != parser.ParseString(templateRoleDefinition.Description))
            {
                siteRoleDefinition.Description = parser.ParseString(templateRoleDefinition.Description);
                isDirty = true;
            }
            var templateBasePermissions = new BasePermissions();
            templateRoleDefinition.Permissions.ForEach(p => templateBasePermissions.Set(p));
            if (siteRoleDefinition.BasePermissions != templateBasePermissions)
            {
                isDirty = true;
                foreach (var permission in templateRoleDefinition.Permissions)
                {
                    siteRoleDefinition.BasePermissions.Set(permission);
                }
            }
            if (isDirty)
            {
                scope.LogDebug("Updating role definition {0}", parser.ParseString(templateRoleDefinition.Name));
                siteRoleDefinition.Update();
                web.Context.ExecuteQueryRetry();
            }
        }

        private static Group UpdateSpGroup(Web web, string parsedGroupTitle, string parsedGroupDescription, SiteGroup siteGroup,
            string parsedGroupOwner, IEnumerable<Group> allGroups, PnPMonitoredScope scope)
        {
            Group spGroup;
            spGroup = web.SiteGroups.GetByName(parsedGroupTitle);
            web.Context.Load(spGroup,
                g => g.Title,
                g => g.Description,
                g => g.AllowMembersEditMembership,
                g => g.AllowRequestToJoinLeave,
                g => g.AutoAcceptRequestToJoinLeave,
                g => g.Owner.LoginName);
            web.Context.ExecuteQueryRetry();
            var isDirty = false;
            if (!String.IsNullOrEmpty(spGroup.Description) && spGroup.Description != parsedGroupDescription)
            {
                spGroup.Description = parsedGroupDescription;
                isDirty = true;
            }
            if (spGroup.AllowMembersEditMembership != siteGroup.AllowMembersEditMembership)
            {
                spGroup.AllowMembersEditMembership = siteGroup.AllowMembersEditMembership;
                isDirty = true;
            }
            if (spGroup.AllowRequestToJoinLeave != siteGroup.AllowRequestToJoinLeave)
            {
                spGroup.AllowRequestToJoinLeave = siteGroup.AllowRequestToJoinLeave;
                isDirty = true;
            }
            if (spGroup.AutoAcceptRequestToJoinLeave != siteGroup.AutoAcceptRequestToJoinLeave)
            {
                spGroup.AutoAcceptRequestToJoinLeave = siteGroup.AutoAcceptRequestToJoinLeave;
                isDirty = true;
            }
            if (spGroup.Owner.LoginName != parsedGroupOwner)
            {
                if (parsedGroupTitle != parsedGroupOwner)
                {
                    Principal ownerPrincipal = allGroups.FirstOrDefault(gr => gr.LoginName == parsedGroupOwner);
                    if (ownerPrincipal == null)
                    {
                        ownerPrincipal = web.EnsureUser(parsedGroupOwner);
                    }
                    spGroup.Owner = ownerPrincipal;
                }
                else
                {
                    spGroup.Owner = spGroup;
                }
                isDirty = true;
            }
            if (isDirty)
            {
                scope.LogDebug("Updating existing group {0}", spGroup.Title);
                spGroup.Update();
                web.Context.ExecuteQueryRetry();
            }
            return spGroup;
        }

        private static Group CreateSpGroup(Web web, TokenParser parser, PnPMonitoredScope scope, string parsedGroupTitle,
            string parsedGroupDescription, string parsedGroupOwner, SiteGroup siteGroup, IEnumerable<Group> allGroups)
        {
            Group spGroup;
            scope.LogDebug("Creating group {0}", parsedGroupTitle);
            spGroup = web.AddGroup(
                parsedGroupTitle,
                parsedGroupDescription,
                parsedGroupTitle == parsedGroupOwner);
            spGroup.AllowMembersEditMembership = siteGroup.AllowMembersEditMembership;
            spGroup.AllowRequestToJoinLeave = siteGroup.AllowRequestToJoinLeave;
            spGroup.AutoAcceptRequestToJoinLeave = siteGroup.AutoAcceptRequestToJoinLeave;

            if (parsedGroupTitle != parsedGroupOwner)
            {
                Principal ownerPrincipal = allGroups.FirstOrDefault(gr => gr.LoginName == parsedGroupOwner);
                if (ownerPrincipal == null)
                {
                    ownerPrincipal = web.EnsureUser(parsedGroupOwner);
                }
                spGroup.Owner = ownerPrincipal;
            }
            spGroup.Update();
            web.Context.Load(spGroup, g => g.Id, g => g.Title);
            web.Context.ExecuteQueryRetry();
            parser.AddToken(new GroupIdToken(web, spGroup.Title, spGroup.Id));
            return spGroup;
        }

        private static void AddUserToGroup(Web web, Group group, IEnumerable<User> members, PnPMonitoredScope scope, TokenParser parser)
        {
            if (members.Any())
            {
                scope.LogDebug("Adding users to group {0}", group.Title);
                try
                {
                    foreach (var user in members)
                    {
                        var parsedUserName = parser.ParseString(user.Name);
                        scope.LogDebug("Adding user {0}", parsedUserName);

                        if (parsedUserName.Contains("#ext#"))
                        {
                            var externalUser = web.SiteUsers.FirstOrDefault(u => u.LoginName.Equals(parsedUserName));

                            if (externalUser == null)
                            {
                                scope.LogInfo($"Skipping external user {parsedUserName}");
                            }
                            else
                            {
                                group.Users.AddUser(externalUser);
                            }
                        }
                        else
                        {
                            try
                            {
                                var existingUser = web.EnsureUser(parsedUserName);
                                web.Context.ExecuteQueryRetry();
                                group.Users.AddUser(existingUser);
                            }
                            catch (Exception ex)
                            {
                                scope.LogWarning(ex, "Failed to EnsureUser {0}", parsedUserName);
                            }
                        }
                    }
                    web.Context.ExecuteQueryRetry();
                }
                catch (Exception ex)
                {
                    scope.LogError(CoreResources.Provisioning_ObjectHandlers_SiteSecurity_Add_users_failed_for_group___0_____1_____2_, group.Title, ex.Message, ex.StackTrace);
                    throw;
                }
            }
        }


        public override ProvisioningTemplate ExtractObjects(Web web, ProvisioningTemplate template,
            ProvisioningTemplateCreationInformation creationInfo)
        {

            using (var scope = new PnPMonitoredScope(this.Name))
            {
                web.EnsureProperties(w => w.HasUniqueRoleAssignments, w => w.Title);

                // Changed by Paolo Pialorsi to embrace the new sub-site attributes for break role inheritance and copy role assignments
                // if this is a sub site then we're not creating security entities as by default security is inherited from the root site
                if (web.IsSubSite() && !web.HasUniqueRoleAssignments)
                {
                    return template;
                }

                var ownerGroup = web.AssociatedOwnerGroup;
                var memberGroup = web.AssociatedMemberGroup;
                var visitorGroup = web.AssociatedVisitorGroup;
                web.Context.ExecuteQueryRetry();

                if (!ownerGroup.ServerObjectIsNull.Value)
                {
                    web.Context.Load(ownerGroup, o => o.Id, o => o.Users, o => o.Title);
                }
                if (!memberGroup.ServerObjectIsNull.Value)
                {
                    web.Context.Load(memberGroup, o => o.Id, o => o.Users, o => o.Title);
                }
                if (!visitorGroup.ServerObjectIsNull.Value)
                {
                    web.Context.Load(visitorGroup, o => o.Id, o => o.Users, o => o.Title);
                }
                web.Context.ExecuteQueryRetry();

                List<int> associatedGroupIds = new List<int>();
                var owners = new List<User>();
                var members = new List<User>();
                var visitors = new List<User>();
                if (!ownerGroup.ServerObjectIsNull.Value)
                {
                    associatedGroupIds.Add(ownerGroup.Id);
                    foreach (var member in ownerGroup.Users)
                    {
                        owners.Add(new User() {Name = member.LoginName});
                    }
                }
                if (!memberGroup.ServerObjectIsNull.Value)
                {
                    associatedGroupIds.Add(memberGroup.Id);
                    foreach (var member in memberGroup.Users)
                    {
                        members.Add(new User() {Name = member.LoginName});
                    }
                }
                if (!visitorGroup.ServerObjectIsNull.Value)
                {
                    associatedGroupIds.Add(visitorGroup.Id);
                    foreach (var member in visitorGroup.Users)
                    {
                        visitors.Add(new User() {Name = member.LoginName});
                    }
                }
                var siteSecurity = new SiteSecurity();
                siteSecurity.AdditionalOwners.AddRange(owners);
                siteSecurity.AdditionalMembers.AddRange(members);
                siteSecurity.AdditionalVisitors.AddRange(visitors);

                var query = from user in web.SiteUsers
                    where user.IsSiteAdmin
                    select user;
                var allUsers = web.Context.LoadQuery(query);

                web.Context.ExecuteQueryRetry();

                var admins = new List<User>();
                foreach (var member in allUsers)
                {
                    admins.Add(new User() {Name = member.LoginName});
                }
                siteSecurity.AdditionalAdministrators.AddRange(admins);

                if (creationInfo.IncludeSiteGroups)
                {
                    web.Context.Load(web.SiteGroups,
                        o => o.IncludeWithDefaultProperties(
                            gr => gr.Title,
                            gr => gr.AllowMembersEditMembership,
                            gr => gr.AutoAcceptRequestToJoinLeave,
                            gr => gr.AllowRequestToJoinLeave,
                            gr => gr.Description,
                            gr => gr.Users.Include(u => u.LoginName),
                            gr => gr.OnlyAllowMembersViewMembership,
                            gr => gr.Owner.LoginName,
                            gr => gr.RequestToJoinLeaveEmailSetting
                            ));

                    web.Context.ExecuteQueryRetry();

                    if (web.IsSubSite())
                    {
                        WriteMessage(
                            "You are requesting to export sitegroups from a subweb. Notice that ALL sitegroups from the site collection are included in the result.",
                            ProvisioningMessageType.Warning);
                    }
                    foreach (var group in web.SiteGroups.AsEnumerable().Where(o => !associatedGroupIds.Contains(o.Id)))
                    {

                        scope.LogDebug("Processing group {0}", group.Title);
                        var siteGroup = new SiteGroup()
                        {
                            Title = group.Title.Replace(web.Title, "{sitename}"),
                            AllowMembersEditMembership = group.AllowMembersEditMembership,
                            AutoAcceptRequestToJoinLeave = group.AutoAcceptRequestToJoinLeave,
                            AllowRequestToJoinLeave = group.AllowRequestToJoinLeave,
                            Description = group.Description,
                            OnlyAllowMembersViewMembership = group.OnlyAllowMembersViewMembership,
                            Owner = ReplaceGroupTokens(web, group.Owner.LoginName),
                            RequestToJoinLeaveEmailSetting = group.RequestToJoinLeaveEmailSetting
                        };
                        try
                        {
                            foreach (var member in group.Users)
                            {
                                scope.LogDebug("Processing member {0} of group {0}", member.LoginName, group.Title);
                                siteGroup.Members.Add(new User() {Name = member.LoginName});
                            }
                            siteSecurity.SiteGroups.Add(siteGroup);
                        }
                        catch (Exception ee)
                        {
                            scope.LogError(ee.StackTrace);
                            scope.LogError(ee.Message);
                            scope.LogError(ee.InnerException.StackTrace);
                        }
                    }
                }

                var webRoleDefinitions =
                    web.Context.LoadQuery(web.RoleDefinitions.Include(r => r.Name, r => r.Description,
                        r => r.BasePermissions, r => r.RoleTypeKind));
                web.Context.ExecuteQueryRetry();

                if (web.HasUniqueRoleAssignments)
                {

                    var permissionKeys = Enum.GetNames(typeof (PermissionKind));
                    if (!web.IsSubSite())
                    {
                        foreach (var webRoleDefinition in webRoleDefinitions)
                        {
                            if (webRoleDefinition.RoleTypeKind == RoleType.None)
                            {
                                scope.LogDebug("Processing custom role definition {0}", webRoleDefinition.Name);
                                var modelRoleDefinitions = new Model.RoleDefinition();

                                modelRoleDefinitions.Description = webRoleDefinition.Description;
                                modelRoleDefinitions.Name = webRoleDefinition.Name;

                                foreach (var permissionKey in permissionKeys)
                                {
                                    scope.LogDebug("Processing custom permissionKey definition {0}", permissionKey);
                                    var permissionKind =
                                        (PermissionKind) Enum.Parse(typeof (PermissionKind), permissionKey);
                                    if (webRoleDefinition.BasePermissions.Has(permissionKind))
                                    {
                                        modelRoleDefinitions.Permissions.Add(permissionKind);
                                    }
                                }
                                siteSecurity.SiteSecurityPermissions.RoleDefinitions.Add(modelRoleDefinitions);
                            }
                            else
                            {
                                scope.LogDebug("Skipping OOTB role definition {0}", webRoleDefinition.Name);
                            }
                        }
                    }
                    var webRoleAssignments = web.Context.LoadQuery(web.RoleAssignments.Include(
                        r => r.RoleDefinitionBindings.Include(
                            rd => rd.Name,
                            rd => rd.RoleTypeKind),
                        r => r.Member.LoginName,
                        r => r.Member.PrincipalType));

                    web.Context.ExecuteQueryRetry();

                    foreach (var webRoleAssignment in webRoleAssignments)
                    {
                        scope.LogDebug("Processing Role Assignment {0}", webRoleAssignment.ToString());
                        if (webRoleAssignment.Member.PrincipalType == PrincipalType.SharePointGroup
                            && !creationInfo.IncludeSiteGroups)
                            continue;

                        if (webRoleAssignment.Member.LoginName != "Excel Services Viewers")
                        {
                            foreach (var roleDefinition in webRoleAssignment.RoleDefinitionBindings)
                            {
                                if (roleDefinition.RoleTypeKind != RoleType.Guest)
                                {
                                    var modelRoleAssignment = new Model.RoleAssignment();
                                    var roleDefinitionValue = roleDefinition.Name;
                                    if (roleDefinition.RoleTypeKind != RoleType.None)
                                    {
                                        // Replace with token
                                        roleDefinitionValue = $"{{roledefinition:{roleDefinition.RoleTypeKind}}}";
                                    }
                                    modelRoleAssignment.RoleDefinition = roleDefinitionValue;
                                    if (webRoleAssignment.Member.PrincipalType == PrincipalType.SharePointGroup)
                                    {
                                        modelRoleAssignment.Principal = ReplaceGroupTokens(web,
                                            webRoleAssignment.Member.LoginName);
                                    }
                                    else
                                    {
                                        modelRoleAssignment.Principal = webRoleAssignment.Member.LoginName;
                                    }
                                    siteSecurity.SiteSecurityPermissions.RoleAssignments.Add(modelRoleAssignment);
                                }
                            }
                        }
                    }
                }

                template.Security = siteSecurity;

                // If a base template is specified then use that one to "cleanup" the generated template model
                if (creationInfo.BaseTemplate != null)
                {
                    template = CleanupEntities(template, creationInfo.BaseTemplate);

                }

                return template;
            }
        }

        private string ReplaceGroupTokens(Web web, string loginName)
        {
            if (!web.AssociatedOwnerGroup.ServerObjectIsNull.Value)
            {
                loginName = loginName.Replace(web.AssociatedOwnerGroup.Title, "{associatedownergroup}");
            }
            if (!web.AssociatedMemberGroup.ServerObjectIsNull.Value)
            {
                loginName = loginName.Replace(web.AssociatedMemberGroup.Title, "{associatedmembergroup}");
            }
            if (!web.AssociatedVisitorGroup.ServerObjectIsNull.Value)
            {
                loginName = loginName.Replace(web.AssociatedVisitorGroup.Title, "{associatedvisitorgroup}");
            }
            if (!string.IsNullOrEmpty(web.Title))
            {
                loginName = loginName.Replace(web.Title, "{sitename}");
            }
            return loginName;
        }

        private ProvisioningTemplate CleanupEntities(ProvisioningTemplate template, ProvisioningTemplate baseTemplate)
        {
            foreach (var user in baseTemplate.Security.AdditionalAdministrators)
            {
                int index = template.Security.AdditionalAdministrators.FindIndex(f => f.Name.Equals(user.Name));

                if (index > -1)
                {
                    template.Security.AdditionalAdministrators.RemoveAt(index);
                }
            }

            foreach (var user in baseTemplate.Security.AdditionalMembers)
            {
                int index = template.Security.AdditionalMembers.FindIndex(f => f.Name.Equals(user.Name));

                if (index > -1)
                {
                    template.Security.AdditionalMembers.RemoveAt(index);
                }
            }

            foreach (var user in baseTemplate.Security.AdditionalOwners)
            {
                int index = template.Security.AdditionalOwners.FindIndex(f => f.Name.Equals(user.Name));

                if (index > -1)
                {
                    template.Security.AdditionalOwners.RemoveAt(index);
                }
            }

            foreach (var user in baseTemplate.Security.AdditionalVisitors)
            {
                int index = template.Security.AdditionalVisitors.FindIndex(f => f.Name.Equals(user.Name));

                if (index > -1)
                {
                    template.Security.AdditionalVisitors.RemoveAt(index);
                }
            }

            foreach (var baseSiteGroup in baseTemplate.Security.SiteGroups)
            {
                var templateSiteGroup = template.Security.SiteGroups.FirstOrDefault(sg => sg.Title == baseSiteGroup.Title);
                if (templateSiteGroup != null)
                {
                    if (templateSiteGroup.Equals(baseSiteGroup))
                    {
                        template.Security.SiteGroups.Remove(templateSiteGroup);
                    }
                }
            }

            foreach (var baseRoleDef in baseTemplate.Security.SiteSecurityPermissions.RoleDefinitions)
            {
                var templateRoleDef = template.Security.SiteSecurityPermissions.RoleDefinitions.FirstOrDefault(rd => rd.Name == baseRoleDef.Name);
                if (templateRoleDef != null)
                {
                    if (templateRoleDef.Equals(baseRoleDef))
                    {
                        template.Security.SiteSecurityPermissions.RoleDefinitions.Remove(templateRoleDef);
                    }
                }
            }

            foreach (var baseRoleAssignment in baseTemplate.Security.SiteSecurityPermissions.RoleAssignments)
            {
                var templateRoleAssignments = template.Security.SiteSecurityPermissions.RoleAssignments.Where(ra => ra.Principal == baseRoleAssignment.Principal).ToList();
                foreach (var templateRoleAssignment in templateRoleAssignments)
                {
                    if (templateRoleAssignment.Equals(baseRoleAssignment))
                    {
                        template.Security.SiteSecurityPermissions.RoleAssignments.Remove(templateRoleAssignment);
                    }
                }
            }

            return template;
        }

        public override bool WillProvision(Web web, ProvisioningTemplate template)
        {
            if (!_willProvision.HasValue)
            {
                if (template.Security.BreakRoleInheritance)
                {
                    _willProvision = true;
                    return _willProvision.Value;
                }

                _willProvision = (template.Security.AdditionalAdministrators.Any() ||
                                  template.Security.AdditionalMembers.Any() ||
                                  template.Security.AdditionalOwners.Any() ||
                                  template.Security.AdditionalVisitors.Any() ||
                                  template.Security.SiteGroups.Any() ||
                                  template.Security.SiteSecurityPermissions.RoleAssignments.Any() ||
                                  template.Security.SiteSecurityPermissions.RoleDefinitions.Any());
                if (_willProvision == true)
                {
                    // if not subweb and site inheritance is not broken
                    if (web.IsSubSite() && web.EnsureProperty(w => w.HasUniqueRoleAssignments) == false)
                    {
                        _willProvision = false;
                    }
                }
            }

            return _willProvision.Value;

        }

        public override bool WillExtract(Web web, ProvisioningTemplate template, ProvisioningTemplateCreationInformation creationInfo)
        {
            if (!_willExtract.HasValue)
            {
                if (web.IsSubSite() && web.EnsureProperty(w => w.HasUniqueRoleAssignments))
                {
                    _willExtract = true;
                }
                else
                {
                    _willExtract = !web.IsSubSite();
                }
            }
            return _willExtract.Value;
        }
    }
}