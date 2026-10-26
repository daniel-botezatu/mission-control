import importlib.util,json,tempfile,unittest
from pathlib import Path
from types import SimpleNamespace
spec=importlib.util.spec_from_file_location('manage',Path(__file__).parents[1]/'manage.py'); manage=importlib.util.module_from_spec(spec); spec.loader.exec_module(manage)
class InstallerTests(unittest.TestCase):
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory(); self.root=Path(self.tmp.name); self.exe=self.root/'mcp.exe'; self.exe.write_bytes(b'synthetic'); self.bundle=self.root/'bundle'
  manage.prepare(SimpleNamespace(output=str(self.bundle),mcp=str(self.exe),data_dir=str(self.root/'data'),github=True,github_token_env='MC_TEST_TOKEN',slack_client_id='123.456'))
 def tearDown(self): self.tmp.cleanup()
 def args(self,host): return SimpleNamespace(target=str(self.root/('config.toml' if host=='codex' else '.mcp.json')),host=host,bundle=str(self.bundle))
 def test_hosts_share_workflow_but_have_distinct_registration(self):
  a=(self.bundle/'claude/skills/developer-memory/SKILL.md').read_text(encoding='utf-8'); b=(self.bundle/'codex/skills/developer-memory/SKILL.md').read_text(encoding='utf-8')
  shared=(Path(__file__).parents[1]/'shared/developer-memory.md').read_text(encoding='utf-8'); self.assertIn(shared,a); self.assertIn(shared,b)
  self.assertNotIn('context: fork',a); self.assertNotIn('context: fork',b)
  agent=(self.bundle/'claude/agents/memory-reader.md').read_text(encoding='utf-8')
  plugin=json.loads((self.bundle/'claude/.claude-plugin/plugin.json').read_text(encoding='utf-8'))
  agent_name=agent.split('name: ',1)[1].splitlines()[0]
  self.assertIn('Agent('+plugin['name']+':'+agent_name+')',a)
  header=agent.split('---')[1]
  self.assertNotIn('mc_slack',header); self.assertNotIn('mc_github',header)
  self.assertIn('disallowedTools: Bash, Write, Edit, NotebookEdit, Agent, Task, Skill',header)
  tools=json.loads((self.bundle/'claude/read-only-agent-tools.json').read_text(encoding='utf-8')); self.assertFalse(any('send_message' in tool or 'Bash' in tool or 'Edit' in tool for tool in tools))
  config=json.loads((self.bundle/'claude/external.mcp.json').read_text(encoding='utf-8')); self.assertEqual('true',config['mcpServers']['mc_github']['headers']['X-MCP-Readonly'])
 def test_claude_package_filters_slack_results_with_the_memory_server(self):
  import re
  hooks=json.loads((self.bundle/'claude/hooks/hooks.json').read_text(encoding='utf-8'))['hooks']['PostToolUse'][0]
  for name in ['mcp__claude_ai_Slack__slack_read_thread','mcp__mc_slack__slack_search_public','mcp__plugin_slack_slack__slack_read_channel','mcp__slack__slack_search_public_and_private']:
   self.assertTrue(re.fullmatch(hooks['matcher'],name),name)
  self.assertFalse(re.fullmatch(hooks['matcher'],'mcp__plugin_mission-control_history__search_history'))
  self.assertEqual([str(self.exe)],[hook['command'] for hook in hooks['hooks']]); self.assertEqual(['filter-tool-output'],hooks['hooks'][0]['args'])
  self.assertFalse((self.bundle/'codex/hooks').exists())
 def test_codex_preserves_unrelated_config_and_roundtrips(self):
  args=self.args('codex'); target=Path(args.target); initial='model = "existing"\n[mcp_servers.other]\ncommand = "existing"\n'; target.write_text(initial)
  manage.modify_config(args); manage.modify_config(args); self.assertIn(initial,target.read_text(encoding='utf-8')); manage.modify_config(args,True); self.assertEqual(initial,target.read_text(encoding='utf-8'))
 def test_private_slack_preference_survives_update_and_can_be_revoked(self):
  args=SimpleNamespace(output=str(self.bundle),mcp=str(self.exe),data_dir=str(self.root/'data'),github=False,github_token_env='MC_TEST_TOKEN',slack_client_id=None,private_slack='allow')
  manage.prepare(args); args.private_slack=None; manage.prepare(args)
  self.assertEqual('allow',json.loads((self.bundle/'installation.json').read_text())['privateSlack'])
  self.assertIn('explicitly authorised', (self.bundle/'claude/agents/memory-reader.md').read_text())
  args.private_slack='public'; manage.prepare(args)
  self.assertNotIn('explicitly authorised', (self.bundle/'claude/agents/memory-reader.md').read_text())
 def test_timezone_survives_updates_and_is_in_server_args_and_skill(self):
  args=SimpleNamespace(output=str(self.bundle),mcp=str(self.exe),data_dir=str(self.root/'data'),github=False,github_token_env='MC_TEST_TOKEN',slack_client_id=None,time_zone='Europe/Amsterdam')
  manage.prepare(args); args.time_zone=None; manage.prepare(args)
  self.assertEqual('Europe/Amsterdam',json.loads((self.bundle/'installation.json').read_text())['timeZone'])
  server=json.loads((self.bundle/'claude/.mcp.json').read_text())['mcpServers']['history']
  self.assertEqual(['--time-zone','Europe/Amsterdam'],server['args'][-2:])
  text=(self.bundle/'codex/skills/developer-memory/SKILL.md').read_text()
  self.assertIn('Configured recap timezone: Europe/Amsterdam',text)
 def test_claude_preserves_other_servers_and_roundtrips(self):
  args=self.args('claude'); target=Path(args.target); initial={'mcpServers':{'other':{'command':'existing'}},'other':True}; target.write_text(json.dumps(initial)); manage.modify_config(args); manage.modify_config(args); manage.modify_config(args,True); self.assertEqual(initial,json.loads(target.read_text(encoding='utf-8')))
 def test_codex_update_preserves_native_plugin_policy(self):
  args=self.args('codex'); target=Path(args.target); manage.modify_config(args)
  policy='\n[plugins."mission-control@mission-control-local"]\nenabled = true\n'
  target.write_text(target.read_text(encoding='utf-8')+policy,encoding='utf-8')
  manage.modify_config(args)
  self.assertIn(policy,target.read_text(encoding='utf-8'))
  manage.modify_config(args,True)
  self.assertEqual(policy,target.read_text(encoding='utf-8'))
 def test_refuses_server_collision(self):
  for host in ['claude','codex']:
   args=self.args(host); target=Path(args.target); text='[mcp_servers.mc_github]\nurl="https://example.invalid"\n' if host=='codex' else json.dumps({'mcpServers':{'mc_github':{'command':'other'}}}); target.write_text(text)
   with self.assertRaises(ValueError): manage.modify_config(args)
   self.assertEqual(text,target.read_text(encoding='utf-8'))
 def test_refuses_user_edited_entries(self):
  for host in ['claude','codex']:
   args=self.args(host); manage.modify_config(args); target=Path(args.target); edited=target.read_text(encoding='utf-8').replace('mc_github','edited_github'); target.write_text(edited)
   with self.assertRaises(ValueError): manage.modify_config(args,True)
   self.assertEqual(edited,target.read_text(encoding='utf-8'))
 def test_journal_recovers_either_side_of_write(self):
  args=self.args('codex'); target=Path(args.target); manage.modify_config(args); state=manage.read_state(target); before=target.read_bytes(); after=before+b'\n# changed\n'
  journal={'owner':manage.OWNER,'pending':{'before':manage.digest(before),'after':manage.digest(after),'previous':state,'next':state}}
  manage.write_atomic(manage.config_state(target),manage.dump(journal)); self.assertEqual(state,manage.read_state(target))
  manage.write_atomic(manage.config_state(target),manage.dump(journal)); target.write_bytes(after); self.assertEqual(state,manage.read_state(target))
 def test_package_removal_preserves_unrelated_files(self):
  other=self.bundle/'personal.txt'; other.write_text('keep'); manage.remove_package(SimpleNamespace(output=str(self.bundle))); self.assertEqual('keep',other.read_text(encoding='utf-8')); self.assertFalse((self.bundle/'claude/.mcp.json').exists())
 def test_refuses_edited_package_removal(self):
  target=self.bundle/'codex/plugin.json'; target.write_text('edited')
  with self.assertRaises(ValueError): manage.remove_package(SimpleNamespace(output=str(self.bundle)))
 def test_rejects_path_escape(self):
  state=self.bundle/'.mission-control-owned.json'; state.write_text(manage.dump({'owner':manage.OWNER,'files':{'../outside':manage.digest(b'')}}))
  with self.assertRaises(ValueError): manage.owned_files(self.bundle,{})
 def test_remove_without_ownership_does_not_touch_json(self):
  args=self.args('claude');target=Path(args.target);target.write_text('{"unrelated": true}')
  manage.modify_config(args,True);self.assertEqual('{"unrelated": true}',target.read_text(encoding='utf-8'))
 def test_removal_host_mismatch_is_rejected(self):
  args=self.args('codex'); manage.modify_config(args); args.host='claude'
  with self.assertRaises(ValueError): manage.modify_config(args,True)
if __name__=='__main__': unittest.main()
