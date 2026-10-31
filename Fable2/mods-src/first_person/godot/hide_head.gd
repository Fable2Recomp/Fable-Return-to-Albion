extends Node
## First Person: the hero's head goes while the camera is in it, so the body stays in view.
##
## The camera (mod/camera.lua) sits at the head's collision point. The head, hair and hat would
## fill the view, so a SkeletonModifier3D on the hero's body skeleton shrinks the neck bone (and
## with it the head) to nothing after the game has posed it, for drawing only: the game's own
## pose is never written, and the shadow proxy has its own skeleton (Engine_ShadowSkeletons), so
## the shadow keeps its head. The head hides only while the camera is within `head_radius` of it, which is first
## person: the game's own look mode and this mod's camera alike, never the third-person camera.

const BODY_SKELETON := "Skeleton"   # Engine_EntitySkeleton's node; the shadow's is "ShadowBones"


class HeadHider extends SkeletonModifier3D:
	var head := -1    # where the camera is measured from
	var bone := -1    # what is shrunk: the neck, so the skin weighted to it goes too
	var radius := 0.6

	func _process_modification_with_delta(_delta: float) -> void:
		var skeleton := get_skeleton()
		var camera := get_viewport().get_camera_3d() if is_inside_tree() else null
		if skeleton == null or camera == null or bone < 0 or head < 0:
			return
		var eyes := skeleton.global_transform * skeleton.get_bone_global_pose(head).origin
		if eyes.distance_to(camera.global_position) < radius:
			skeleton.set_bone_pose_scale(bone, Vector3.ONE * 0.001)


var host: Object
var hider: HeadHider
var skeleton: Skeleton3D
var wait := 0.0


func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")


func _process(delta: float) -> void:
	if hider != null and is_instance_valid(hider):
		hider.radius = host.setting("first_person.head_radius", 0.6)
	wait -= delta
	if wait > 0.0:
		return
	wait = 0.5
	var body := _hero_skeleton()
	if body == skeleton and hider != null and is_instance_valid(hider):
		return
	if hider != null and is_instance_valid(hider):
		hider.queue_free()
	hider = null
	skeleton = body
	if skeleton == null:
		return
	var head := _head_bone(skeleton)
	if head < 0:
		push_warning("first_person: no head bone on the hero's skeleton")
		return
	# The back of the head and the neck are skinned to the neck bone: shrinking only the head left
	# them stretched up under the camera, in view at a wide field of view.
	var bone := head
	var parent := skeleton.get_bone_parent(head)
	if parent >= 0 and skeleton.get_bone_name(parent).to_lower().ends_with("neck"):
		bone = parent
	hider = HeadHider.new()
	hider.name = "FirstPersonHeadHider"
	hider.head = head
	hider.bone = bone
	skeleton.add_child(hider)
	print("first_person: hiding bone %s in first person" % skeleton.get_bone_name(bone))


func _exit_tree() -> void:
	if hider != null and is_instance_valid(hider):
		hider.queue_free()


func _hero_skeleton() -> Skeleton3D:
	var hero: int = host.local_hero()
	if hero == 0:
		return null
	var node: Node = host.node_for(hero)
	if node == null:
		return null
	for found in node.find_children(BODY_SKELETON, "Skeleton3D", true, false):
		return found
	return null


## The topmost bone named for the head (Shadow_Head): the face bones, jaw, eyes, hair and hat hang
## off it.
func _head_bone(body: Skeleton3D) -> int:
	for b in body.get_bone_count():
		var bone_name := body.get_bone_name(b).to_lower()
		if not bone_name.ends_with("head"):
			continue
		var parent := body.get_bone_parent(b)
		if parent < 0 or not body.get_bone_name(parent).to_lower().ends_with("head"):
			return b
	return -1
